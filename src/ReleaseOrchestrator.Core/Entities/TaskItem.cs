namespace ReleaseOrchestrator.Core.Entities;

public class TaskItem
{
    public Guid Id { get; set; }
    public string ExternalId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public DateTime? ClosedAt { get; set; }
    public Guid TrackerConnectionId { get; set; }
    public byte[]? RowVersion { get; set; }

    public TrackerConnection TrackerConnection { get; set; } = null!;

    /// <summary>Tasks this one waits on — deploy those first.</summary>
    public ICollection<TaskDependency> Dependencies { get; set; } = [];

    /// <summary>Tasks waiting on this one — deploy this first.</summary>
    public ICollection<TaskDependency> Dependents { get; set; } = [];

    public ICollection<MergeRequest> MergeRequests { get; set; } = [];
}
