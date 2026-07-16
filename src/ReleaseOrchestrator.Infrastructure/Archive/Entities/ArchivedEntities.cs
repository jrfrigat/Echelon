namespace ReleaseOrchestrator.Infrastructure.Archive.Entities;

public class ArchivedTask
{
    public Guid Id { get; set; }
    public string ExternalId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public DateTime? ClosedAt { get; set; }
    public string? DependenciesJson { get; set; }
    public DateTime ArchivedAt { get; set; }
}

public class ArchivedMergeRequest
{
    public Guid Id { get; set; }
    public string ExternalId { get; set; } = string.Empty;
    public string RepositoryName { get; set; } = string.Empty;
    public string SourceBranch { get; set; } = string.Empty;
    public string TargetBranch { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string? TaskExternalId { get; set; }
    public DateTime? ClosedAt { get; set; }
    public DateTime ArchivedAt { get; set; }
}

public class ArchivedReleasePlan
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Version { get; set; } = string.Empty;
    public string PlanJson { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime ArchivedAt { get; set; }
}
