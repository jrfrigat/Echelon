namespace ReleaseOrchestrator.Application.Services;

public interface ITrackerApiClient
{
    Task<TrackerIssueInfo?> GetIssueAsync(string apiUrl, string orgId, string token, string issueKey, CancellationToken ct);
    Task<IReadOnlyList<TrackerIssueDependency>> GetIssueDependenciesAsync(string apiUrl, string orgId, string token, string issueKey, CancellationToken ct);
}

public record TrackerIssueInfo(string Key, string Summary, string StatusKey, DateTime? ResolvedAt);
public record TrackerIssueDependency(string IssueKey, string DependsOnKey);
