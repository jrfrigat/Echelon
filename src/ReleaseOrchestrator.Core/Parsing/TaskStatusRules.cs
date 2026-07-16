namespace ReleaseOrchestrator.Core.Parsing;

/// <summary>
/// Which tracker statuses count as closed. Single source of truth: the ingress and the
/// consumer previously kept separate lists that disagreed on "resolved", so a resolved
/// task was closed enough to trigger a replan but never got a ClosedAt — leaving it
/// permanently ineligible for archiving.
/// </summary>
public static class TaskStatusRules
{
    private static readonly HashSet<string> ClosedStatuses =
        new(StringComparer.OrdinalIgnoreCase) { "closed", "cancelled", "rejected", "resolved" };

    public static bool IsClosed(string? statusKey) =>
        statusKey is not null && ClosedStatuses.Contains(statusKey.Trim());
}
