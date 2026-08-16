namespace ReleaseOrchestrator.Application.Services;

/// <summary>
/// A mutually exclusive, expiring claim on a named job across every replica.
/// </summary>
/// <remarks>
/// Background work here is registered in every replica, so without this each pass runs N times:
/// N archive cycles competing to delete the same rows, N reconciliation sweeps asking the tracker
/// for the same tasks. Correctness survives it - the work is idempotent - but the deadlocks and
/// the multiplied API calls are real.
///
/// Expiring rather than released-on-exit: a replica that is killed cannot release anything, and a
/// lease nobody can take again is worse than no lease at all.
/// </remarks>
public interface IDistributedLease
{
    /// <summary>Tries to take the lease for <paramref name="name"/>.</summary>
    /// <param name="name">Job name, shared by every replica competing for it.</param>
    /// <param name="duration">
    /// How long the claim survives without renewal. Must outlast the work, or a second replica
    /// starts while the first is still running.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A handle to dispose when done, or <c>null</c> when another replica holds it.</returns>
    Task<IAsyncDisposable?> TryAcquireAsync(string name, TimeSpan duration, CancellationToken ct);
}
