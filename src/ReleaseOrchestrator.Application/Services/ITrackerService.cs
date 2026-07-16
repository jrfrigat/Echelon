namespace ReleaseOrchestrator.Application.Services;

public interface ITrackerService
{
    /// <summary>
    /// Reads a task and its dependency links from the tracker into the local model.
    /// This is the only thing that produces task edges for the release plan.
    /// </summary>
    /// <returns>True when the dependency edges changed, so the caller can replan only then.</returns>
    Task<bool> SyncTaskAsync(Guid trackerConnectionId, string externalTaskId, CancellationToken ct = default);

    Task<string?> ParseTaskIdFromBranchAsync(string branchName, CancellationToken ct = default);
}
