using Echelon.Core.Enums;

namespace Echelon.Core.Parsing;

/// <summary>
/// Rules over the normalized <see cref="MergeRequestStatus"/>.
/// </summary>
/// <remarks>
/// What remains here operates only on values the domain owns. Translating a provider's raw state
/// string - GitLab's <c>opened</c>/<c>merged</c>/<c>closed</c> - used to live here too, which put
/// one vendor's vocabulary in the domain; that mapping now belongs to each VCS adapter, which hands
/// back a <see cref="MergeRequestStatus"/> already resolved. A coarse "ready-for-deploy label"
/// promotion lived here as well; it is gone - deploy readiness is a per-environment rule over signals
/// (<see cref="ReadinessSignals"/>, <see cref="ReadinessResolver"/>), not a single label promoting a
/// merge request to a status.
/// </remarks>
public static class MergeRequestStatusResolver
{
    /// <summary>Terminal states are decided by the VCS and never by a label.</summary>
    /// <param name="status">The status to test.</param>
    /// <returns><c>true</c> when the merge request can no longer change on its own.</returns>
    public static bool IsTerminal(MergeRequestStatus status) =>
        status is MergeRequestStatus.Merged or MergeRequestStatus.Closed;

    /// <summary>
    /// Statuses nothing writes any more, kept declared only so rows written before they were
    /// retired still materialise.
    /// </summary>
    /// <param name="status">The status to test.</param>
    /// <returns><c>true</c> when the status is history rather than something to set.</returns>
    /// <remarks>
    /// <see cref="MergeRequestStatus.Reviewed"/> and <see cref="MergeRequestStatus.ReadyForDeploy"/>
    /// existed for the label promotion that decided deployability; that became a per-environment
    /// readiness rule evaluated at launch, and nothing has assigned either since. Storing one now
    /// would be worse than useless: the value means nothing to any reader, while the manual-status
    /// flag it sets stops observation from ever correcting it, so the merge request sits in a status
    /// that neither the operator nor the system can act on. Writers check this and refuse.
    /// </remarks>
    public static bool IsRetired(MergeRequestStatus status) =>
        status is MergeRequestStatus.Reviewed or MergeRequestStatus.ReadyForDeploy;

    /// <summary>
    /// Resolves the status to store for an open merge request: simply <see cref="MergeRequestStatus.Opened"/>,
    /// unless an operator pinned the status, in which case the pinned status is kept.
    /// </summary>
    /// <param name="isStatusManual">True when an operator pinned the status; it is then preserved.</param>
    /// <param name="currentStatus">The stored status, kept when the status is manual.</param>
    /// <returns>The status to store.</returns>
    /// <remarks>
    /// There is no label promotion any more: an open merge request is Opened, and whether it may deploy
    /// to a given environment is decided at launch by that environment's readiness rule, not by a
    /// status. Both ingestion paths route through here so they cannot disagree.
    /// </remarks>
    public static MergeRequestStatus ResolveOpenStatus(bool isStatusManual, MergeRequestStatus currentStatus) =>
        isStatusManual ? currentStatus : MergeRequestStatus.Opened;
}
