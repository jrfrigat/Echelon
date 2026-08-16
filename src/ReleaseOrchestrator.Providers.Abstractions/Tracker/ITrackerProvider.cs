namespace ReleaseOrchestrator.Providers.Abstractions.Tracker;

/// <summary>
/// A tracker, already bound to one connection.
/// </summary>
/// <remarks>
/// As with <see cref="Vcs.IVcsProvider"/>, no method takes an endpoint, a token or an
/// organization id. <c>orgId</c> in particular used to sit in this contract, which meant the
/// contract described Yandex.Tracker rather than "a tracker" - Jira has no such concept. It now
/// lives in typed options inside the adapter that needs it.
/// </remarks>
public interface ITrackerProvider
{
    /// <summary>What this particular connection can do.</summary>
    TrackerCapabilities Capabilities { get; }

    /// <summary>Reads one issue.</summary>
    /// <param name="issueKey">The issue key.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The issue, or <c>null</c> when the tracker does not have it.</returns>
    Task<TrackerIssue?> GetIssueAsync(string issueKey, CancellationToken ct);

    /// <summary>
    /// Whether a status key means the issue is finished with.
    /// </summary>
    /// <param name="statusKey">The tracker's status key; may be null or blank.</param>
    /// <returns><c>true</c> when the status is a closed one for this tracker.</returns>
    /// <remarks>
    /// On the provider because the set of closed statuses is a tracker's own vocabulary -
    /// Yandex.Tracker ships a fixed handful, Jira lets every project define its own workflow - and
    /// there is no universal list to put in the domain. Two copies of this rule once disagreed
    /// about "resolved", which left such tasks closed enough to trigger a replan but never
    /// archivable; keeping one owner per tracker is the fix.
    /// </remarks>
    bool IsClosedStatus(string? statusKey);
}
