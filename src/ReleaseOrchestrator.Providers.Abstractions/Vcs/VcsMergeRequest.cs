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
///
/// Every timestamp here is <see cref="DateTimeKind.Utc"/>, and converting is the adapter's job for
/// the same reason translating the state dialect is. Providers stamp an offset; deserialising that
/// straight into a <see cref="DateTime"/> yields <see cref="DateTimeKind.Local"/>, which SQL Server
/// stores at the wrong instant without complaining and PostgreSQL rejects outright — so a
/// <c>Kind=Local</c> value that crosses this boundary is a bug nobody sees until the second database
/// runs. Adapters parse into <see cref="DateTimeOffset"/> and hand back <c>UtcDateTime</c>.
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
/// <param name="CreatedAt">When the merge request was opened. Always UTC.</param>
/// <param name="MergedAt">When it was merged, when it was. Always UTC.</param>
/// <param name="Labels">
/// Labels currently on the merge request. Empty when the provider cannot report labels — check
/// <see cref="VcsCapabilities.SupportsMergeRequestLabels"/> to tell "no labels" from "cannot say".
/// </param>
/// <param name="PipelineStatus">
/// The latest CI pipeline result for the head, as the provider reports it (e.g. <c>success</c>), or
/// null when the provider does not report one. A readiness rule can require it as <c>pipeline:*</c>.
/// </param>
public sealed record VcsMergeRequest(
    string Id,
    string SourceBranch,
    string TargetBranch,
    MergeRequestStatus? Status,
    string Title,
    DateTime CreatedAt,
    DateTime? MergedAt,
    IReadOnlyList<string> Labels,
    string? PipelineStatus = null);
