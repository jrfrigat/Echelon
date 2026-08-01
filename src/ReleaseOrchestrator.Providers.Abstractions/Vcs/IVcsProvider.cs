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

    /// <summary>Lists the branches of a repository.</summary>
    /// <param name="projectPath">The repository, as this provider identifies it.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The branches; empty when there are none, or when this provider does not report them.</returns>
    /// <remarks>
    /// Default-empty so a provider that cannot list branches does not have to implement it; check
    /// <see cref="VcsCapabilities.SupportsBranches"/> to tell "no branches" from "cannot say", exactly
    /// as with labels. A branch with no merge request is work that started and has not landed, which is
    /// what lets a parent task be held back while a child is still in progress.
    /// </remarks>
    Task<IReadOnlyList<VcsBranch>> GetBranchesAsync(string projectPath, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<VcsBranch>>([]);

    // Task-key extraction used to live here, as a provider dialect. It is now a per-connection rule
    // (source + pattern) applied by the ingestion through Core.Parsing.TaskKeyExtractor, so a provider
    // no longer owns the link format -- an operator configures it. See TaskLinkSettings.
}
