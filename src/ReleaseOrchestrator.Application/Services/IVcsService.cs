using ReleaseOrchestrator.Application.DTOs;

namespace ReleaseOrchestrator.Application.Services;

public interface IVcsService
{
    Task SyncMergeRequestAsync(Guid repositoryId, string externalMrId, CancellationToken ct = default);
    Task<MergeRequestDto?> GetMergeRequestAsync(string connectionName, string projectPath, string iid, CancellationToken ct = default);
}
