namespace ReleaseOrchestrator.Infrastructure.Archive.Entities;

public class ArchivedTask
{
    public Guid Id { get; set; }
    public string ExternalId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public DateTime? ClosedAt { get; set; }
    public string? DependenciesJson { get; set; }

    /// <summary>When the orchestrator first stored the task, carried over so the archive keeps its arrival.</summary>
    public DateTime? FirstSeenAt { get; set; }

    /// <summary>How it arrived, carried over with <see cref="FirstSeenAt"/>.</summary>
    public string? FirstSeenSource { get; set; }

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

    /// <summary>
    /// The merge request's labels at archive time, canonical (comma-joined). Copied for the same
    /// reason <see cref="Status"/> is: the source row is hard-deleted, and the snapshot should read
    /// the same as the row it replaced. The change history is preserved separately by the label
    /// journal, which is FK-free and pruned on its own schedule.
    /// </summary>
    public string Labels { get; set; } = string.Empty;

    /// <summary>
    /// When the merge request was opened.
    /// </summary>
    /// <remarks>
    /// Copied because archiving hard-deletes the source row. Without it the "merge request opened"
    /// entry on a task's history is not hidden by archiving, it is destroyed — the only copy of that
    /// timestamp goes with the row.
    /// </remarks>
    public DateTime CreatedAt { get; set; }

    /// <summary>When the MR was merged. Null for an MR closed without merging.</summary>
    public DateTime? MergedAt { get; set; }

    /// <summary>
    /// When the MR was closed without merging. Null for a merged MR — the two outcomes are
    /// mutually exclusive, so a report that wants "when did this MR end" reads whichever is set.
    /// </summary>
    public DateTime? ClosedAt { get; set; }

    public DateTime ArchivedAt { get; set; }
}
