using ReleaseOrchestrator.Core.Enums;

namespace ReleaseOrchestrator.Core.Parsing;

/// <summary>
/// Rules over the normalized <see cref="MergeRequestStatus"/>.
/// </summary>
/// <remarks>
/// <para>
/// What remains here operates only on values the domain owns. Translating a provider's raw state
/// string — GitLab's <c>opened</c>/<c>merged</c>/<c>closed</c> — used to live here too, which put
/// one vendor's vocabulary in the domain; that mapping now belongs to each VCS adapter, which
/// hands back a <see cref="MergeRequestStatus"/> already resolved.
/// </para>
/// <para>
/// Label-driven promotion stays because it is not a dialect: the label to look for is per
/// connection (<c>VcsConnection.ReadyForDeployLabel</c>) and the rule applies to whatever list of
/// labels a provider reports. Both the webhook ingress and the sync path route through here — two
/// copies of this rule is what let the same merge request end up with different statuses
/// depending on its arrival path.
/// </para>
/// </remarks>
public static class MergeRequestStatusResolver
{
    /// <summary>Terminal states are decided by the VCS and never by a label.</summary>
    /// <param name="status">The status to test.</param>
    /// <returns><c>true</c> when the merge request can no longer change on its own.</returns>
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
    /// <returns>The status to store.</returns>
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
