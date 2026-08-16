using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace ReleaseOrchestrator.Infrastructure.Persistence.Models;

/// <summary>
/// "<see cref="DependentTask"/> depends on <see cref="DependsOnTask"/>" - so the latter deploys first.
/// </summary>
/// <remarks>
/// Which navigation pairs with which is the most consequential mapping in this model, and it was
/// wrong. Bound the other way round, <c>task.Dependencies</c> yields the rows naming the task as
/// the prerequisite, so every test of the form "is this row's DependsOnTaskId my task?" answers
/// yes: every dependency edge collapses into a self-loop and the merge request is stranded. It
/// compiled, it ran, and the plan came out wrong in silence.
/// </remarks>
// One row per ordered pair: a tracker reporting the same link twice must not double the edge.
[Index(nameof(DependentTaskId), nameof(DependsOnTaskId), IsUnique = true, Name = "IX_TaskDependency_Dependent_DependsOn")]
public class TaskDependency
{
    /// <summary>Primary key.</summary>
    [Key]
    public Guid Id { get; set; }

    /// <summary>The task that waits.</summary>
    public Guid DependentTaskId { get; set; }

    /// <summary>The task waited on.</summary>
    public Guid DependsOnTaskId { get; set; }

    /// <summary>
    /// The waiting task. Pairs with <see cref="TaskItem.Dependencies"/> - the rows naming it as
    /// the dependent, which are the tasks it waits on.
    /// </summary>
    [ForeignKey(nameof(DependentTaskId))]
    [InverseProperty(nameof(TaskItem.Dependencies))]
    [DeleteBehavior(DeleteBehavior.Restrict)]
    public TaskItem DependentTask { get; set; } = null!;

    /// <summary>
    /// The task waited on. Pairs with <see cref="TaskItem.Dependents"/> - the rows naming it as
    /// the prerequisite, which are the tasks waiting on it.
    /// </summary>
    [ForeignKey(nameof(DependsOnTaskId))]
    [InverseProperty(nameof(TaskItem.Dependents))]
    [DeleteBehavior(DeleteBehavior.Restrict)]
    public TaskItem DependsOnTask { get; set; } = null!;
}
