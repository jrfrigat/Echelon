namespace ReleaseOrchestrator.Providers.Abstractions.Vcs;

/// <summary>
/// A VCS, already bound to one connection.
/// </summary>
/// <remarks>
/// <para>
/// No method takes an API URL, a token or an organization identifier: the instance was built for
/// a connection and already knows them. That is the whole point of the port — the caller states
/// what it wants, not how the provider is configured.
/// </para>
/// <para>
/// Deliberately small. These are the calls the planner actually makes today; Renovate's platform
/// interface reached forty methods by growing one need at a time, not by being designed up front,
/// and guessing at the rest before a second provider exists would only produce a shape the second
/// provider does not fit.
/// </para>
/// </remarks>
public interface IVcsProvider
{
    /// <summary>What this particular connection can do — see <see cref="VcsCapabilities"/>.</summary>
    VcsCapabilities Capabilities { get; }

    /// <summary>Reads one merge request.</summary>
    /// <param name="projectPath">The repository, as this provider identifies it.</param>
    /// <param name="mergeRequestId">The merge request's provider-scoped id.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The merge request, or <c>null</c> when the provider does not have it.</returns>
    Task<VcsMergeRequest?> GetMergeRequestAsync(string projectPath, string mergeRequestId, CancellationToken ct);

    /// <summary>Lists the open merge requests of a repository.</summary>
    /// <param name="projectPath">The repository, as this provider identifies it.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The open merge requests; empty when there are none.</returns>
    Task<IReadOnlyList<VcsMergeRequest>> GetOpenMergeRequestsAsync(string projectPath, CancellationToken ct);

    /// <summary>
    /// Extracts a tracker issue key from a branch name, e.g. <c>feature/PROJ-123-thing</c>.
    /// </summary>
    /// <param name="branchName">The branch name; may be null or blank.</param>
    /// <returns>The issue key, or <c>null</c> when the branch does not name one.</returns>
    /// <remarks>
    /// On the provider because the key format is a dialect, not a universal rule: <c>PROJ-123</c>
    /// is the Jira and Yandex.Tracker shape, while GitHub Issues would want <c>#123</c>. It sits
    /// behind this port so that the sync path can ask for the answer without knowing whose
    /// dialect produced it.
    /// </remarks>
    string? ParseTaskKeyFromBranch(string? branchName);
}
