namespace ReleaseOrchestrator.Providers.YandexTracker;

/// <summary>
/// Which Yandex.Tracker statuses count as closed.
/// </summary>
/// <remarks>
/// <para>
/// This list is Yandex.Tracker's alone. Jira lets each project define its own workflow, so it has
/// no fixed list at all and its adapter will have to ask the server or be configured. Held in the
/// domain, this read like a universal rule that every tracker would have to be bent to fit.
/// </para>
/// <para>
/// One owner per tracker, deliberately: the ingress and the sync consumer once kept separate
/// lists that disagreed about <c>resolved</c>, so a resolved task was closed enough to trigger a
/// replan but never got a <c>ClosedAt</c> — leaving it permanently ineligible for archiving.
/// </para>
/// </remarks>
public static class YandexTrackerStatusRules
{
    private static readonly HashSet<string> ClosedStatuses =
        new(StringComparer.OrdinalIgnoreCase) { "closed", "cancelled", "rejected", "resolved" };

    /// <summary>Whether a status key means the issue is finished with.</summary>
    /// <param name="statusKey">The tracker's status key; may be null or blank.</param>
    /// <returns><c>true</c> when the status is one of Yandex.Tracker's closed statuses.</returns>
    public static bool IsClosed(string? statusKey) =>
        statusKey is not null && ClosedStatuses.Contains(statusKey.Trim());
}
