using ReleaseOrchestrator.Core.Enums;

namespace ReleaseOrchestrator.Core.Parsing;

/// <summary>
/// Single source of truth for turning VCS state + labels into a <see cref="MergeRequestStatus"/>.
/// Both the webhook ingress and the VCS sync route through here; keeping two copies of this
/// rule is what let the same MR end up with different statuses depending on its arrival path.
/// </summary>
public static class MergeRequestStatusResolver
{
    /// <summary>Maps a raw VCS state string. Unknown states are not guessed at.</summary>
    public static MergeRequestStatus? FromVcsState(string? state) => state?.ToLowerInvariant() switch
    {
        "opened" or "reopened" => MergeRequestStatus.Opened,
        "merged" => MergeRequestStatus.Merged,
        "closed" => MergeRequestStatus.Closed,
        _ => null
    };

    /// <summary>Terminal states are decided by the VCS and never by a label.</summary>
    public static bool IsTerminal(MergeRequestStatus status) =>
        status is MergeRequestStatus.Merged or MergeRequestStatus.Closed;

    /// <summary>
    /// Resolves the status to store for an open merge request (README §5: an MR is deployable
    /// when it carries the connection's ready-for-deploy label).
    /// </summary>
    /// <param name="labels">Labels currently on the MR.</param>
    /// <param name="readyForDeployLabel">The connection's marker label; null disables promotion.</param>
    /// <param name="isStatusManual">True when an operator pinned the status; label rules defer to them.</param>
    /// <param name="currentStatus">Status already stored, preserved when an operator pinned it.</param>
    public static MergeRequestStatus ResolveOpenStatus(
        IEnumerable<string>? labels,
        string? readyForDeployLabel,
        bool isStatusManual,
        MergeRequestStatus currentStatus)
    {
        if (isStatusManual) return currentStatus;
        if (string.IsNullOrWhiteSpace(readyForDeployLabel)) return MergeRequestStatus.Opened;

        var hasLabel = labels?.Any(l =>
            string.Equals(l?.Trim(), readyForDeployLabel.Trim(), StringComparison.OrdinalIgnoreCase)) ?? false;

        return hasLabel ? MergeRequestStatus.ReadyForDeploy : MergeRequestStatus.Opened;
    }
}
