using ReleaseOrchestrator.Core.Enums;

namespace ReleaseOrchestrator.Core.Entities;

public class MergeRequest
{
    public Guid Id { get; set; }
    public string ExternalId { get; set; } = string.Empty;
    public string SourceBranch { get; set; } = string.Empty;
    public string TargetBranch { get; set; } = string.Empty;
    public Guid RepositoryId { get; set; }
    public Guid? TaskId { get; set; }
    public MergeRequestStatus Status { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? MergedAt { get; set; }

    public Repository Repository { get; set; } = null!;
    public TaskItem? Task { get; set; }
    public ICollection<StageItem> StageItems { get; set; } = [];
}
