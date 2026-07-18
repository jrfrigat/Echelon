using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using ReleaseOrchestrator.Core.Enums;

namespace ReleaseOrchestrator.Infrastructure.Persistence.Models;

/// <summary>An operator edit to a <see cref="RolloutPlan"/>, stored as a delta so it survives recalculation.</summary>
/// <remarks>
/// The plan is recomputed from the atlas; edits kept only in the materialised result would be lost
/// on the next recalculation. Keeping them as deltas (kind + JSON payload) lets the plan track the
/// atlas and still honour the operator's intent (docs/issues/006-per-task-planning.md).
/// </remarks>
public class PlanOverride
{
    /// <summary>Primary key.</summary>
    [Key]
    public Guid Id { get; set; }

    /// <summary>The plan this edit applies to.</summary>
    public Guid RolloutPlanId { get; set; }

    /// <summary>What kind of edit this is. See <see cref="PlanOverrideKind"/>.</summary>
    public PlanOverrideKind Kind { get; set; }

    /// <summary>The edit's parameters as JSON; shape depends on <see cref="Kind"/>.</summary>
    [Required]
    public string Payload { get; set; } = string.Empty;

    /// <summary>The plan. Cascade: an override means nothing without its plan.</summary>
    [ForeignKey(nameof(RolloutPlanId))]
    [InverseProperty(nameof(Models.RolloutPlan.Overrides))]
    public RolloutPlan RolloutPlan { get; set; } = null!;
}
