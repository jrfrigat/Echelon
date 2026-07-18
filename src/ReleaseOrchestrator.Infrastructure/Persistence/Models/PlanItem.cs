using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace ReleaseOrchestrator.Infrastructure.Persistence.Models;

/// <summary>A merge request attached to a task node in a <see cref="RolloutPlan"/>.</summary>
[Index(nameof(PlanTaskNodeId), nameof(MergeRequestId), IsUnique = true, Name = "IX_PlanItem_Node_Mr")]
[Index(nameof(MergeRequestId), Name = "IX_PlanItem_MergeRequestId")]
public class PlanItem
{
    /// <summary>Primary key.</summary>
    [Key]
    public Guid Id { get; set; }

    /// <summary>The task node this merge request hangs off.</summary>
    public Guid PlanTaskNodeId { get; set; }

    /// <summary>The merge request.</summary>
    public Guid MergeRequestId { get; set; }

    /// <summary>
    /// A deploy strategy chosen for this merge request specifically, overriding the repository's
    /// default. Null uses <c>Repository.DeployStrategyKey</c>.
    /// </summary>
    [MaxLength(100)]
    public string? DeployStrategyKeyOverride { get; set; }

    /// <summary>
    /// A pinned order for this merge request within its task, the escape hatch for cases the
    /// repository-ordering policy does not express. Null lets the policy decide.
    /// </summary>
    public int? IntraTaskOrder { get; set; }

    /// <summary>True when an operator forced this merge request into the plan.</summary>
    public bool ManualInclusion { get; set; }

    /// <summary>The task node. Cascade: an item means nothing without its node.</summary>
    [ForeignKey(nameof(PlanTaskNodeId))]
    [InverseProperty(nameof(Models.PlanTaskNode.Items))]
    public PlanTaskNode PlanTaskNode { get; set; } = null!;

    /// <summary>The merge request. Restrict: archiving must not orphan a plan (as with StageItem).</summary>
    [ForeignKey(nameof(MergeRequestId))]
    [DeleteBehavior(DeleteBehavior.Restrict)]
    public MergeRequest MergeRequest { get; set; } = null!;
}
