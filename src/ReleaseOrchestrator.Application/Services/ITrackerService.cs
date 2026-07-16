namespace ReleaseOrchestrator.Application.Services;

public interface ITrackerService
{
    Task SyncTaskAsync(Guid trackerConnectionId, string externalTaskId, CancellationToken ct = default);
    Task<string?> ParseTaskIdFromBranchAsync(string branchName, CancellationToken ct = default);
}
