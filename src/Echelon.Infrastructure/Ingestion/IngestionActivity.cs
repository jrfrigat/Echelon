using System.Collections.Concurrent;
using Echelon.Application.DTOs;
using Echelon.Core.Enums;

namespace Echelon.Infrastructure.Ingestion;

/// <summary>
/// What the ingestion is doing, kept in memory so an operator can see it.
/// </summary>
/// <remarks>
/// <para>
/// The question this answers is the one the logs answer badly: is anything actually reading the
/// tracker and the VCS, and has anything arrived? A sweep that finds nothing looks exactly like a
/// sweep that cannot see anything, and a webhook that never comes looks exactly like a quiet week -
/// until someone asks why a task never appeared.
/// </para>
/// <para>
/// In memory and per replica, on purpose. These are facts about a running process: which pass is in
/// flight, how long the last one took, what has arrived since start. Persisting them would make a
/// second source of truth for something the process already knows, and would answer a different
/// question (history) than the one being asked (now). A restart clears it, which is correct - the
/// counters describe this process's work.
/// </para>
/// <para>
/// Singleton, and written from background threads, so every field is either interlocked or guarded by
/// the per-worker lock. Reads take a snapshot rather than handing out the live state.
/// </para>
/// </remarks>
/// <param name="clock">The clock, so tests can age entries without waiting.</param>
public sealed class IngestionActivity(TimeProvider clock)
{
    private readonly ConcurrentDictionary<IngestionWorker, WorkerState> _workers = new();
    private readonly ConcurrentDictionary<IngestionSignal, SignalState> _signals = new();
    private readonly ConcurrentDictionary<(IngestionConnectionKind Kind, string Name), IngestionConnectionDto> _connections = new();
    private readonly DateTime _startedAt = clock.GetUtcNow().UtcDateTime;

    /// <summary>Announces a worker before its first pass, so a disabled one is still on the screen.</summary>
    /// <param name="worker">The worker.</param>
    /// <param name="enabled">Whether configuration lets it run at all.</param>
    /// <param name="intervalSeconds">How often it wakes.</param>
    public void Declare(IngestionWorker worker, bool enabled, int intervalSeconds)
    {
        var state = _workers.GetOrAdd(worker, _ => new WorkerState());
        lock (state.Gate)
        {
            state.Enabled = enabled;
            state.IntervalSeconds = intervalSeconds;
        }
    }

    /// <summary>Records that a worker is waiting for the lease another replica holds.</summary>
    /// <param name="worker">The worker.</param>
    public void NotLeader(IngestionWorker worker)
    {
        var state = _workers.GetOrAdd(worker, _ => new WorkerState());
        lock (state.Gate)
        {
            state.HoldsLease = false;
        }
    }

    /// <summary>Begins a pass. Dispose the result, or the worker stays "running" for ever.</summary>
    /// <param name="worker">The worker starting work.</param>
    public IngestionRun Begin(IngestionWorker worker)
    {
        var state = _workers.GetOrAdd(worker, _ => new WorkerState());
        lock (state.Gate)
        {
            state.HoldsLease = true;
            state.Running = true;
            state.LastStartedAt = clock.GetUtcNow().UtcDateTime;
            state.LastFinishedAt = null;
            state.Emitted = 0;
        }

        return new IngestionRun(this, worker);
    }

    /// <summary>Records something that arrived, whether a webhook brought it or a poll found it.</summary>
    /// <param name="signal">What kind of change.</param>
    /// <param name="count">How many; defaults to one.</param>
    public void Observed(IngestionSignal signal, int count = 1)
    {
        var state = _signals.GetOrAdd(signal, _ => new SignalState());
        lock (state.Gate)
        {
            state.Count += count;
            state.LastAt = clock.GetUtcNow().UtcDateTime;
        }
    }

    /// <summary>Records the result of polling one connection.</summary>
    /// <param name="kind">Which side it sits on.</param>
    /// <param name="name">The connection's name.</param>
    /// <param name="emitted">What the poll produced.</param>
    /// <param name="extra">The second count: branches seen, or tasks newly discovered.</param>
    /// <param name="failure">Why it could not be read, when it could not.</param>
    public void Polled(IngestionConnectionKind kind, string name, int emitted, int extra, string? failure)
    {
        _connections[(kind, name)] = new IngestionConnectionDto(
            kind, name, clock.GetUtcNow().UtcDateTime, emitted, extra, failure);
    }

    /// <summary>Everything, as of now.</summary>
    public IngestionStatusDto Snapshot()
    {
        var workers = _workers
            .OrderBy(w => w.Key)
            .Select(w => w.Value.ToDto(w.Key))
            .ToList();

        // Every signal is listed, including the ones at zero: "no merge request has ever arrived" is
        // the answer someone is looking for, and an absent row reads as "not measured".
        var signals = Enum.GetValues<IngestionSignal>()
            .Select(s => _signals.TryGetValue(s, out var state)
                ? state.ToDto(s)
                : new IngestionSignalDto(s, 0, null))
            .ToList();

        var connections = _connections.Values
            .OrderBy(c => c.Kind)
            .ThenBy(c => c.Name, StringComparer.Ordinal)
            .ToList();

        return new IngestionStatusDto(clock.GetUtcNow().UtcDateTime, _startedAt, workers, signals, connections);
    }

    private void Finish(IngestionWorker worker, int emitted, Exception? failure)
    {
        var state = _workers.GetOrAdd(worker, _ => new WorkerState());
        lock (state.Gate)
        {
            var finished = clock.GetUtcNow().UtcDateTime;
            state.Running = false;
            state.LastFinishedAt = finished;
            state.LastDurationMs = state.LastStartedAt is { } started
                ? (int)Math.Max(0, (finished - started).TotalMilliseconds)
                : null;
            state.Emitted = emitted;
            state.Passes++;
            state.Outcome = failure is null ? IngestionOutcome.Ok : IngestionOutcome.Failed;
            state.Error = failure?.Message;
        }
    }

    /// <summary>One pass of one worker, from start to whatever ended it.</summary>
    /// <remarks>
    /// A struct-like scope rather than paired calls: a worker that threw between "started" and
    /// "finished" would otherwise be shown as running for ever, which is exactly the state an operator
    /// would misread as "it is working on it".
    /// </remarks>
    public sealed class IngestionRun(IngestionActivity activity, IngestionWorker worker) : IDisposable
    {
        private Exception? _failure;
        private int _emitted;
        private bool _finished;

        /// <summary>Adds to what this pass produced.</summary>
        /// <param name="count">How many more.</param>
        public void Emitted(int count) => _emitted += count;

        /// <summary>Records that the pass failed, and why.</summary>
        /// <param name="failure">The exception that ended it.</param>
        public void Failed(Exception failure) => _failure = failure;

        /// <inheritdoc/>
        public void Dispose()
        {
            if (_finished) return;

            _finished = true;
            activity.Finish(worker, _emitted, _failure);
        }
    }

    private sealed class WorkerState
    {
        public Lock Gate { get; } = new();

        public bool Enabled { get; set; } = true;
        public bool HoldsLease { get; set; } = true;
        public bool Running { get; set; }
        public int IntervalSeconds { get; set; }
        public DateTime? LastStartedAt { get; set; }
        public DateTime? LastFinishedAt { get; set; }
        public int? LastDurationMs { get; set; }
        public IngestionOutcome Outcome { get; set; } = IngestionOutcome.None;
        public string? Error { get; set; }
        public long Passes { get; set; }
        public int Emitted { get; set; }

        public IngestionWorkerDto ToDto(IngestionWorker worker)
        {
            lock (Gate)
            {
                var state = !Enabled ? IngestionRunState.Disabled
                    : Running ? IngestionRunState.Running
                    : !HoldsLease ? IngestionRunState.NotLeader
                    : IngestionRunState.Idle;

                return new IngestionWorkerDto(
                    worker, state, IntervalSeconds, LastStartedAt, LastFinishedAt,
                    LastDurationMs, Outcome, Error, Passes, Emitted);
            }
        }
    }

    private sealed class SignalState
    {
        public Lock Gate { get; } = new();

        public long Count { get; set; }
        public DateTime? LastAt { get; set; }

        public IngestionSignalDto ToDto(IngestionSignal signal)
        {
            lock (Gate)
            {
                return new IngestionSignalDto(signal, Count, LastAt);
            }
        }
    }
}
