using ReleaseOrchestrator.Core.Enums;

namespace ReleaseOrchestrator.Providers.GitLab;

/// <summary>
/// GitLab's merge-request state vocabulary, and how it maps onto the domain's.
/// </summary>
/// <remarks>
/// This dictionary is GitLab's alone. GitHub says <c>open</c>/<c>closed</c> and carries merged-ness
/// in a separate flag; Bitbucket says <c>OPEN</c>/<c>MERGED</c>/<c>DECLINED</c>. Held in the domain
/// it read like a universal rule, and the first non-GitLab provider would have had to either match
/// GitLab's spelling or edit the domain to be understood.
/// </remarks>
public static class GitLabMergeRequestState
{
    /// <summary>Maps a raw GitLab state string onto the normalized status.</summary>
    /// <param name="state">The value of a merge request's <c>state</c> field.</param>
    /// <returns>
    /// The normalized status, or <c>null</c> for a state this adapter does not model.
    /// </returns>
    /// <remarks>
    /// Unknown states return null rather than a guess. The caller records nothing and says so:
    /// inferring "probably open" would move a merge request into the release plan on the strength
    /// of a string we did not recognize.
    /// </remarks>
    public static MergeRequestStatus? FromState(string? state) => state?.ToLowerInvariant() switch
    {
        "opened" or "reopened" => MergeRequestStatus.Opened,
        "merged" => MergeRequestStatus.Merged,
        "closed" => MergeRequestStatus.Closed,
        _ => null
    };
}
