using System.Threading.Channels;
using Microsoft.Extensions.Options;
using ReleaseOrchestrator.Application.DTOs;
using ReleaseOrchestrator.Application.Services;

namespace ReleaseOrchestrator.Infrastructure.Audit;

/// <summary>
/// Holds request records between the request that produced them and the writer that stores them.
/// </summary>
/// <remarks>
/// <para>
/// Never blocks and never throws. A full buffer drops the record and counts it; the count is
/// reported to the operator rather than only to a log, because a silent gap in an audit reads as
/// quiet traffic, which is the opposite of what it means.
/// </para>
/// <para>
/// Registered as a singleton and shared by every request in the process.
/// </para>
/// </remarks>
public sealed class RequestAuditBuffer : IRequestAuditSink
{
    private readonly Channel<RequestAuditRecord> _channel;
    private readonly int _anonymousLimit;

    private long _dropped;
    private long _anonymousCapBoundCount;

    // The per-minute anonymous window, packed so it can be swapped atomically: high 32 bits are the
    // minute, low 32 the count. Two fields would need a lock to stay consistent with each other.
    private long _anonymousWindow;

    /// <summary>Creates the buffer.</summary>
    /// <param name="options">Audit settings.</param>
    public RequestAuditBuffer(IOptions<RequestAuditOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var value = options.Value;

        _anonymousLimit = Math.Max(0, value.MaxAnonymousRecordsPerMinute);

        _channel = Channel.CreateBounded<RequestAuditRecord>(
            new BoundedChannelOptions(Math.Max(16, value.BufferCapacity))
            {
                // Wait, paired with TryWrite -- NOT DropWrite, which looks like the obvious choice and
                // is wrong here. Under DropWrite the channel discards the item and TryWrite still
                // returns true, so the drop counter never fires and the page reports "0 dropped"
                // while quietly losing records: the exact silent incompleteness this feature exists
                // to prevent. With Wait, TryWrite returns false when full, which is what the counter
                // needs -- and it still never blocks, because TryWrite never blocks whatever the mode.
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true
            });
    }

    /// <inheritdoc/>
    public void Enqueue(RequestAuditRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        if (record.UserId is null && !TryAdmitAnonymous())
        {
            Interlocked.Increment(ref _anonymousCapBoundCount);
            Interlocked.Increment(ref _dropped);
            return;
        }

        if (!_channel.Writer.TryWrite(record))
            Interlocked.Increment(ref _dropped);
    }

    /// <summary>The reader side, for the background writer.</summary>
    public ChannelReader<RequestAuditRecord> Reader => _channel.Reader;

    /// <summary>
    /// Records that <paramref name="count"/> already-dequeued records were lost.
    /// </summary>
    /// <remarks>
    /// A batch whose write fails is just as absent from the audit as one the buffer refused, and the
    /// operator has the same right to know. Without this the summary reported zero dropped over a
    /// real gap, which reads as "no traffic" - the one interpretation that is never true.
    /// </remarks>
    public void RecordLoss(int count)
    {
        if (count > 0) Interlocked.Add(ref _dropped, count);
    }

    /// <summary>
    /// Records that could not be stored since the process started.
    /// </summary>
    /// <remarks>
    /// Cumulative and never reset: the summary reports it so an operator reading a quiet-looking
    /// window can tell the difference between no traffic and unrecorded traffic.
    /// </remarks>
    public long Dropped => Interlocked.Read(ref _dropped);

    /// <summary>True when the anonymous ceiling has bound at least once.</summary>
    public bool AnonymousCapBound => Interlocked.Read(ref _anonymousCapBoundCount) > 0;

    /// <summary>
    /// Admits an unauthenticated record if this minute's allowance is not spent.
    /// </summary>
    /// <remarks>
    /// A compare-and-swap over a packed (minute, count) pair. Under contention a losing thread
    /// retries against the winner's value, so the count cannot drift; when the minute rolls over the
    /// window resets to one.
    /// </remarks>
    private bool TryAdmitAnonymous()
    {
        if (_anonymousLimit == 0) return false;

        var minute = (int)(DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 60);

        while (true)
        {
            var current = Interlocked.Read(ref _anonymousWindow);
            var currentMinute = (int)(current >> 32);
            var count = (int)(current & 0xFFFFFFFF);

            if (currentMinute != minute)
            {
                var reset = ((long)minute << 32) | 1;
                if (Interlocked.CompareExchange(ref _anonymousWindow, reset, current) == current)
                    return true;
                continue;
            }

            if (count >= _anonymousLimit) return false;

            var incremented = ((long)minute << 32) | (uint)(count + 1);
            if (Interlocked.CompareExchange(ref _anonymousWindow, incremented, current) == current)
                return true;
        }
    }
}
