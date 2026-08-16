using ReleaseOrchestrator.Application.DTOs;

namespace ReleaseOrchestrator.Application.Services;

/// <summary>
/// Reads merge requests from whichever VCS a repository's connection names.
/// </summary>
/// <remarks>
/// The webhook path is the primary source; this port exists to reconcile what webhooks missed -
/// failed deliveries, downtime, a label removal nobody observed. Both paths resolve state through
/// the same rules on purpose: they once kept separate mappings, and the same merge request ended up
/// with a different status depending on which path imported it.
/// </remarks>
public interface IVcsService
{
    /// <summary>
    /// Re-reads one merge request from the provider and reconciles the stored row with it: status,
    /// branches, task link, label set and pipeline result.
    /// </summary>
    /// <param name="repositoryId">The repository the merge request belongs to.</param>
    /// <param name="externalMrId">The provider's own id for the merge request.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <remarks>
    /// Creates the row when the merge request is not stored yet. A merge request the provider no
    /// longer has is left alone rather than deleted - absence from one read is not proof it is gone.
    /// </remarks>
    Task SyncMergeRequestAsync(Guid repositoryId, string externalMrId, CancellationToken ct = default);

    /// <summary>
    /// Reads a stored merge request by its provider coordinates.
    /// </summary>
    /// <param name="connectionName">The VCS connection's name.</param>
    /// <param name="projectPath">The repository's external id, as configured.</param>
    /// <param name="iid">The provider's own id for the merge request.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// The stored merge request, or null when the connection, the repository or the merge request is
    /// unknown here. A local read: it does not call the provider.
    /// </returns>
    Task<MergeRequestDto?> GetMergeRequestAsync(string connectionName, string projectPath, string iid, CancellationToken ct = default);
}
