using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace ReleaseOrchestrator.Infrastructure.Persistence.Models;

/// <summary>A task in the target's dependency closure, as covered by one <see cref="RolloutPlan"/>.</summary>
/// <remarks>
/// The tree the operator sees is these nodes (target at the root, prerequisites as children); the
/// execution order is not -- waves are a merge-request property (docs/issues/004-target-architecture.md).
/// </remarks>
[Index(nameof(RolloutPlanId), nameof(TaskId), IsUnique = true, Name = "IX_PlanTaskNode_Plan_Task")]
public class PlanTaskNode
{
    /// <summary>Primary key.</summary>
    [Key]
    public Guid Id { get; set; }

    /// <summary>The plan this node belongs to.</summary>
    public Guid RolloutPlanId { get; set; }

    /// <summary>The task this node represents.</summary>
    public Guid TaskId { get; set; }

    /// <summary>The plan. Cascade: a node means nothing without its plan.</summary>
    [ForeignKey(nameof(RolloutPlanId))]
    [InverseProperty(nameof(Models.RolloutPlan.Nodes))]
    public RolloutPlan RolloutPlan { get; set; } = null!;

    /// <summary>The task. Restrict: a task in a plan is not deleted from under it.</summary>
    [ForeignKey(nameof(TaskId))]
    [DeleteBehavior(DeleteBehavior.Restrict)]
    public TaskItem Task { get; set; } = null!;

    /// <summary>The merge requests attached to this task node.</summary>
    [InverseProperty(nameof(PlanItem.PlanTaskNode))]
    public ICollection<PlanItem> Items { get; set; } = [];
}
