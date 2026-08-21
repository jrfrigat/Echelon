using Echelon.Core.Enums;
using Echelon.Infrastructure.Ingestion;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Echelon.UnitTests.Ingestion;

/// <summary>
/// What the operations screen reads.
/// </summary>
/// <remarks>
/// The screen exists to answer "is anything actually reading the tracker and the VCS", so the states
/// it can show have to be distinguishable: idle is not the same as switched off, and neither is the
/// same as "another replica holds the sweep". A page that collapses those tells an operator the system
/// is fine while nothing runs.
/// </remarks>
public class IngestionActivityTests
{
    private static readonly DateTime Now = new(2026, 8, 21, 12, 0, 0, DateTimeKind.Utc);

    private static (IngestionActivity Activity, FakeTimeProvider Clock) New()
    {
        var clock = new FakeTimeProvider(Now);
        return (new IngestionActivity(clock), clock);
    }

    [Fact]
    public void ADeclaredWorkerAppearsBeforeItEverRuns()
    {
        var (activity, _) = New();

        activity.Declare(IngestionWorker.VcsPolling, enabled: true, intervalSeconds: 60);

        var worker = Assert.Single(activity.Snapshot().Workers);
        Assert.Equal(IngestionWorker.VcsPolling, worker.Worker);
        Assert.Equal(IngestionRunState.Idle, worker.State);
        Assert.Equal(60, worker.IntervalSeconds);
        Assert.Equal(IngestionOutcome.None, worker.Outcome);
        Assert.Null(worker.LastStartedAt);
    }

    [Fact]
    public void ADisabledWorkerSaysSoRatherThanLookingIdle()
    {
        var (activity, _) = New();

        activity.Declare(IngestionWorker.TrackerPolling, enabled: false, intervalSeconds: 60);

        Assert.Equal(IngestionRunState.Disabled, Assert.Single(activity.Snapshot().Workers).State);
    }

    [Fact]
    public void AWorkerIsRunningUntilItsPassEnds()
    {
        var (activity, _) = New();
        activity.Declare(IngestionWorker.VcsPolling, enabled: true, intervalSeconds: 60);

        var run = activity.Begin(IngestionWorker.VcsPolling);
        Assert.Equal(IngestionRunState.Running, Assert.Single(activity.Snapshot().Workers).State);

        run.Dispose();
        Assert.Equal(IngestionRunState.Idle, Assert.Single(activity.Snapshot().Workers).State);
    }

    [Fact]
    public void APassRecordsWhatItProducedAndHowLongItTook()
    {
        var (activity, clock) = New();
        activity.Declare(IngestionWorker.TrackerPolling, enabled: true, intervalSeconds: 60);

        using (var run = activity.Begin(IngestionWorker.TrackerPolling))
        {
            run.Emitted(3);
            run.Emitted(4);
            clock.Advance(TimeSpan.FromMilliseconds(250));
        }

        var worker = Assert.Single(activity.Snapshot().Workers);
        Assert.Equal(IngestionOutcome.Ok, worker.Outcome);
        Assert.Equal(7, worker.Emitted);
        Assert.Equal(250, worker.LastDurationMs);
        Assert.Equal(1, worker.Passes);
        Assert.Null(worker.Error);
    }

    [Fact]
    public void AFailedPassKeepsTheReason()
    {
        var (activity, _) = New();

        using (var run = activity.Begin(IngestionWorker.VcsPolling))
        {
            run.Failed(new InvalidOperationException("no queue configured"));
        }

        var worker = Assert.Single(activity.Snapshot().Workers);
        Assert.Equal(IngestionOutcome.Failed, worker.Outcome);

        // The message, not just the verdict: "failed" on its own sends an operator to the log.
        Assert.Equal("no queue configured", worker.Error);
    }

    [Fact]
    public void APassThatThrowsDoesNotLeaveTheWorkerRunningForEver()
    {
        var (activity, _) = New();

        try
        {
            using var run = activity.Begin(IngestionWorker.TaskReconciliation);
            throw new InvalidOperationException("boom");
        }
        catch (InvalidOperationException)
        {
            // The using is what has to save this, not the catch.
        }

        Assert.NotEqual(IngestionRunState.Running, Assert.Single(activity.Snapshot().Workers).State);
    }

    [Fact]
    public void AReplicaWithoutTheLeaseSaysSo()
    {
        var (activity, _) = New();
        activity.Declare(IngestionWorker.VcsPolling, enabled: true, intervalSeconds: 60);

        activity.NotLeader(IngestionWorker.VcsPolling);

        // "Idle" would read as "nothing to do"; this reads as "someone else is doing it".
        Assert.Equal(IngestionRunState.NotLeader, Assert.Single(activity.Snapshot().Workers).State);
    }

    [Fact]
    public void EverySignalIsListedEvenAtZero()
    {
        var (activity, _) = New();

        var signals = activity.Snapshot().Signals;

        // An absent row reads as "not measured"; a zero reads as "nothing has arrived", which is the
        // answer someone is looking for.
        Assert.Equal(Enum.GetValues<IngestionSignal>().Length, signals.Count);
        Assert.All(signals, s => Assert.Equal(0, s.Count));
        Assert.All(signals, s => Assert.Null(s.LastAt));
    }

    [Fact]
    public void SignalsCountAndRememberWhenTheyLastArrived()
    {
        var (activity, clock) = New();

        activity.Observed(IngestionSignal.MergeRequestOpened);
        clock.Advance(TimeSpan.FromMinutes(5));
        activity.Observed(IngestionSignal.BranchesObserved, 12);

        var signals = activity.Snapshot().Signals;
        var mrs = signals.Single(s => s.Signal == IngestionSignal.MergeRequestOpened);
        var branches = signals.Single(s => s.Signal == IngestionSignal.BranchesObserved);

        Assert.Equal(1, mrs.Count);
        Assert.Equal(Now, mrs.LastAt);

        // Counted by branch, not by message: one delivery carries a repository's whole list.
        Assert.Equal(12, branches.Count);
        Assert.Equal(Now.AddMinutes(5), branches.LastAt);
    }

    [Fact]
    public void TheLastPollOfAConnectionReplacesTheOneBefore()
    {
        var (activity, clock) = New();

        activity.Polled(IngestionConnectionKind.Vcs, "gitlab", 2, 5, failure: null);
        clock.Advance(TimeSpan.FromMinutes(1));
        activity.Polled(IngestionConnectionKind.Vcs, "gitlab", 0, 0, "token rejected");

        var connection = Assert.Single(activity.Snapshot().Connections);
        Assert.Equal("gitlab", connection.Name);
        Assert.Equal(Now.AddMinutes(1), connection.LastPolledAt);
        Assert.Equal("token rejected", connection.Failure);
    }

    [Fact]
    public void TheSnapshotCarriesTheServerClock()
    {
        var (activity, clock) = New();
        clock.Advance(TimeSpan.FromHours(2));

        var snapshot = activity.Snapshot();

        // The page ages everything against this rather than against the browser, whose clock is
        // rarely the same and would happily report "in 4 seconds".
        Assert.Equal(Now.AddHours(2), snapshot.ServerTimeUtc);
        Assert.Equal(Now, snapshot.StartedAt);
    }
}
