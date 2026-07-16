using ReleaseOrchestrator.Core.Enums;

namespace ReleaseOrchestrator.Providers.Abstractions.Vcs;

/// <summary>
/// A merge request, normalized: whatever the provider called things, this is what the core sees.
/// </summary>
/// <remarks>
/// <see cref="Status"/> is the normalized <see cref="MergeRequestStatus"/> and not the provider's
/// raw state string. Translating the dialect is the adapter's job — GitLab says
/// <c>opened</c>/<c>merged</c>/<c>closed</c>, GitHub says <c>open</c>/<c>closed</c> plus a merged
/// flag, Bitbucket shouts <c>OPEN</c>/<c>MERGED</c>/<c>DECLINED</c>. A raw state crossing this
/// boundary would put one provider's vocabulary into the domain, which is what this assembly
/// exists to prevent.
/// </remarks>
/// <param name="Id">Provider-scoped identifier of the merge request, as the provider spells it.</param>
/// <param name="SourceBranch">Branch being merged from.</param>
/// <param name="TargetBranch">Branch being merged into.</param>
/// <param name="Status">
/// The normalized status, or <c>null</c> when the provider reported a state the adapter does not
/// model. Null means "unknown", never "assume open" — guessing here silently moves merge requests
/// into or out of the release plan.
/// </param>
/// <param name="Title">Human-readable title.</param>
/// <param name="CreatedAt">When the merge request was opened.</param>
/// <param name="MergedAt">When it was merged, when it was.</param>
/// <param name="Labels">
/// Labels currently on the merge request. Empty when the provider cannot report labels — check
/// <see cref="VcsCapabilities.SupportsMergeRequestLabels"/> to tell "no labels" from "cannot say".
/// </param>
public sealed record VcsMergeRequest(
    string Id,
    string SourceBranch,
    string TargetBranch,
    MergeRequestStatus? Status,
    string Title,
    DateTime CreatedAt,
    DateTime? MergedAt,
    IReadOnlyList<string> Labels);
