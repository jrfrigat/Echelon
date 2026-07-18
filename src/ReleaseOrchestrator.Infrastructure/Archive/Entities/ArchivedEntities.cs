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

    /// <summary>When the MR was merged. Null for an MR closed without merging.</summary>
    public DateTime? MergedAt { get; set; }

    /// <summary>
    /// When the MR was closed without merging. Null for a merged MR — the two outcomes are
    /// mutually exclusive, so a report that wants "when did this MR end" reads whichever is set.
    /// </summary>
    public DateTime? ClosedAt { get; set; }

    public DateTime ArchivedAt { get; set; }
}
