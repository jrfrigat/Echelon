using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;
using Echelon.Core.Enums;

namespace Echelon.Infrastructure.Persistence.Models;

/// <summary>An operator edit to a task's plan, stored as a delta so it survives recalculation.</summary>
/// <remarks>
/// <para>
/// The plan is recomputed from the atlas; edits kept only in the materialised result would be lost on
/// the next recalculation. Keeping them as deltas (kind + JSON payload) lets the plan track the atlas
/// and still honour the operator's intent (docs/issues/006-per-task-planning.md).
/// </para>
/// <para>
/// Keyed on the TASK, not on a plan. It used to hang off <c>RolloutPlanId</c>, which defeated the
/// whole design: every ingestion event mints a new plan version, so an edit was orphaned by the very
/// next recalculation - the thing deltas exist to survive. The task is the stable identity a rollout
/// is planned for, so an edit against it outlives any number of rebuilds.
/// </para>
/// </remarks>
// One delta per (task, kind, payload): re-adding an edge that is already pinned is a no-op, not a
// second row, and without this a repeated click grows the table with duplicates that all replay.
[Index(nameof(TaskId), nameof(Kind), Name = "IX_PlanOverride_Task_Kind")]
public class PlanOverride
{
    /// <summary>Primary key.</summary>
    [Key]
    public Guid Id { get; set; }

    /// <summary>The task whose plan this edit applies to.</summary>
    public Guid TaskId { get; set; }

    /// <summary>What kind of edit this is. See <see cref="PlanOverrideKind"/>.</summary>
    public PlanOverrideKind Kind { get; set; }

    /// <summary>The edit's parameters as JSON; shape depends on <see cref="Kind"/>.</summary>
    [Required]
    public string Payload { get; set; } = string.Empty;

    /// <summary>The task. Cascade: an override means nothing without the task it plans.</summary>
    [ForeignKey(nameof(TaskId))]
    [InverseProperty(nameof(TaskItem.PlanOverrides))]
    public TaskItem Task { get; set; } = null!;
}
