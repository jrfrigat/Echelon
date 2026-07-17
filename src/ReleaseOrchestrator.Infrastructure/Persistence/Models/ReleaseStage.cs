using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace ReleaseOrchestrator.Infrastructure.Persistence.Models;

/// <summary>A group of merge requests deployable in parallel, and its place in the order.</summary>
public class ReleaseStage
{
    /// <summary>Primary key.</summary>
    [Key]
    public Guid Id { get; set; }

    /// <summary>The plan this stage belongs to.</summary>
    public Guid PlanId { get; set; }

    /// <summary>Deploy order within the plan, from 1. Unique within a plan — a reorder must renumber every stage.</summary>
    public int Sequence { get; set; }

    /// <summary>Operator-facing label, if any.</summary>
    [MaxLength(300)]
    public string? Name { get; set; }

    /// <summary>True once an operator edited this stage; recalculation then leaves the plan alone.</summary>
    public bool IsManualOverride { get; set; }

    /// <summary>Its plan. Cascade: a stage outside a plan is meaningless.</summary>
    [ForeignKey(nameof(PlanId))]
    [InverseProperty(nameof(ReleasePlan.Stages))]
    [DeleteBehavior(DeleteBehavior.Cascade)]
    public ReleasePlan Plan { get; set; } = null!;

    /// <summary>The merge requests in this stage.</summary>
    [InverseProperty(nameof(StageItem.Stage))]
    public ICollection<StageItem> Items { get; set; } = [];
}
