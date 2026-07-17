using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace ReleaseOrchestrator.Infrastructure.Persistence.Models;

/// <summary>One merge request's place in one stage of a plan.</summary>
// Archiving asks "is this merge request in any plan?" before deleting it, and that question is
// this index.
[Index(nameof(MergeRequestId), Name = "IX_StageItem_MergeRequestId")]
public class StageItem
{
    /// <summary>Primary key.</summary>
    [Key]
    public Guid Id { get; set; }

    /// <summary>The stage it sits in.</summary>
    public Guid StageId { get; set; }

    /// <summary>The merge request.</summary>
    public Guid MergeRequestId { get; set; }

    /// <summary>True when an operator added this by hand rather than the planner selecting it.</summary>
    public bool ManualInclusion { get; set; }

    /// <summary>Its stage. Cascade: an item outside a stage is meaningless.</summary>
    [ForeignKey(nameof(StageId))]
    [InverseProperty(nameof(ReleaseStage.Items))]
    [DeleteBehavior(DeleteBehavior.Cascade)]
    public ReleaseStage Stage { get; set; } = null!;

    /// <summary>
    /// The merge request. Restrict, deliberately: this is what stops archiving from deleting a
    /// merge request that a plan still refers to, which would leave the plan describing rows that
    /// no longer exist.
    /// </summary>
    [ForeignKey(nameof(MergeRequestId))]
    [InverseProperty(nameof(Models.MergeRequest.StageItems))]
    [DeleteBehavior(DeleteBehavior.Restrict)]
    public MergeRequest MergeRequest { get; set; } = null!;
}
