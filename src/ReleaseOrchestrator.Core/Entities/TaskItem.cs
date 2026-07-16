namespace ReleaseOrchestrator.Core.Entities;

public class TaskItem
{
    public Guid Id { get; set; }
    public string ExternalId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public DateTime? ClosedAt { get; set; }
    public Guid TrackerConnectionId { get; set; }

    public TrackerConnection TrackerConnection { get; set; } = null!;
    public ICollection<TaskDependency> Dependencies { get; set; } = [];
    public ICollection<TaskDependency> Dependents { get; set; } = [];
    public ICollection<MergeRequest> MergeRequests { get; set; } = [];
}
