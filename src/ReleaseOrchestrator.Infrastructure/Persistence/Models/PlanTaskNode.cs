using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace ReleaseOrchestrator.Infrastructure.Persistence.Models;

/// <summary>A task in the target's dependency closure, as covered by one <see cref="RolloutPlan"/>.</summary>
/// <remarks>
/// The tree the operator sees is these nodes (target at the root, prerequisites as children); the
/// execution order is not -- waves are a merge-request property.
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

    /// <summary>
    /// The closure tasks this one waits on, as a JSON array of ids — the wait graph as it stood when
    /// the plan was built. Null on a plan stored before it was recorded.
    /// </summary>
    /// <remarks>
    /// Stored for the same reason as <see cref="PlanItem.Wave"/>: the tree an operator approves has to
    /// be the tree the plan version means, and the wait graph moves under it (a policy change, a new
    /// link in the tracker). Recomputing it on read labelled the current answer with an old version.
    ///
    /// JSON rather than a join table on purpose. A join table would need a foreign key to
    /// <c>TaskItem</c>, and every such key is another <c>Restrict</c> pinning a task in place — the
    /// archive is already blocked by exactly that (011 §1.3), and a snapshot column is not worth
    /// widening it.
    /// </remarks>
    public string? DependsOnTaskIdsJson { get; set; }

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
