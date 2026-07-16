namespace ReleaseOrchestrator.Application.Services;

public interface IVcsApiClient
{
    Task<VcsApiMrInfo?> GetMergeRequestAsync(string apiUrl, string token, string projectPath, string iid, CancellationToken ct);
    Task<IReadOnlyList<VcsApiMrInfo>> GetOpenMergeRequestsAsync(string apiUrl, string token, string projectPath, CancellationToken ct);
}

public record VcsApiMrInfo(
    string Iid,
    string SourceBranch,
    string TargetBranch,
    string State,
    string Title,
    DateTime CreatedAt,
    DateTime? MergedAt);
