using ReleaseOrchestrator.Core.Enums;

namespace ReleaseOrchestrator.Core.Parsing;

/// <summary>
/// Rules over the normalized <see cref="MergeRequestStatus"/>.
/// </summary>
/// <remarks>
/// What remains here operates only on values the domain owns. Translating a provider's raw state
/// string — GitLab's <c>opened</c>/<c>merged</c>/<c>closed</c> — used to live here too, which put
/// one vendor's vocabulary in the domain; that mapping now belongs to each VCS adapter, which hands
/// back a <see cref="MergeRequestStatus"/> already resolved. A coarse "ready-for-deploy label"
/// promotion lived here as well; it is gone — deploy readiness is a per-environment rule over signals
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
