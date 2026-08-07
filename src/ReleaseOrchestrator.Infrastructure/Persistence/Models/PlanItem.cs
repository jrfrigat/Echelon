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
    /// The deploy wave this merge request landed in, 1-based, as the planner computed it when the
    /// plan was built. Zero on a plan stored before waves were recorded.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A COMPUTED value, deliberately stored. That is the opposite of the <c>IntraTaskOrder</c> column
    /// this replaces, which tried to store an AUTHORED order here and could not work: these rows are
    /// recreated on every recalculation, and a recalculation happens on every ingestion event, so an
    /// authored value would be gone by the next webhook. Authored ordering lives in
    /// <see cref="PlanOverride"/>, keyed to the task; what belongs here is the result, which is
    /// rewritten together with the rows it describes.
    /// </para>
    /// <para>
    /// It is stored rather than recomputed on read because a plan version has to mean one thing. The
    /// screen, the exported document and the launch all read it here, so an operator approves the
    /// order that will actually run; when all three derived it independently from a moving atlas, the
    /// launch could deploy in an order nobody had seen.
    /// </para>
    /// </remarks>
    public int Wave { get; set; }

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
