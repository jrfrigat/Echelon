namespace ReleaseOrchestrator.Core.Entities;

public class TaskDependency
{
    public Guid Id { get; set; }
    public Guid DependentTaskId { get; set; }
    public Guid DependsOnTaskId { get; set; }

    public TaskItem DependentTask { get; set; } = null!;
    public TaskItem DependsOnTask { get; set; } = null!;
}
