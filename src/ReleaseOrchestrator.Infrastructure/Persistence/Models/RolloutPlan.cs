using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;
using ReleaseOrchestrator.Core.Enums;

namespace ReleaseOrchestrator.Infrastructure.Persistence.Models;

/// <summary>
/// A rollout plan for one target task: the projection of the atlas rooted at that task.
/// </summary>
/// <remarks>
/// Holds at most one active plan PER task, enforced by a filtered unique index scoped to
/// <see cref="TargetTaskId"/> (declared in <c>RolloutPlanConfiguration</c>, because <c>[Index]</c>
/// has no filter). This is the only plan aggregate since the pivot retired the single global
/// release plan (docs/issues/009-admin-and-migration.md).
/// </remarks>
[Index(nameof(CreatedAt), Name = "IX_RolloutPlan_CreatedAt")]
public class RolloutPlan
{
    /// <summary>Primary key.</summary>
    [Key]
    public Guid Id { get; set; }

    /// <summary>The task this plan rolls out. Restrict: a task with a plan is not deleted from under it.</summary>
    public Guid TargetTaskId { get; set; }

    /// <summary>Version label; a generated plan stamps the time it was built.</summary>
    [Required, MaxLength(50)]
    public string Version { get; set; } = string.Empty;

    /// <summary>How the plan came to exist. See <see cref="PlanSource"/>.</summary>
    public PlanSource Source { get; set; }

    /// <summary>Whether it is ready to launch from. See <see cref="PlanStatus"/>.</summary>
    public PlanStatus Status { get; set; }

    /// <summary>The one plan in force for the target task. See the filtered unique index.</summary>
    public bool IsActive { get; set; }

    /// <summary>SHA-256 of the imported document, so an operator can tell whether this is still what they wrote.</summary>
    [MaxLength(64)]
    public string? YamlHash { get; set; }

    /// <summary>
    /// Ordering constraints this plan does not honour, as JSON. Null means it honours every one.
    /// A plan may break a constraint, but it may never report itself clean while doing so
    /// (docs/issues/001-current-state.md, the "never a silent plan" rule).
    /// </summary>
    public string? ConflictsJson { get; set; }

    /// <summary>When the plan was stored.</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>When it was last edited.</summary>
    public DateTime UpdatedAt { get; set; }

    /// <summary>When the planner began reading the atlas this plan was built from (staleness watermark).</summary>
    public DateTime SnapshotStartedAt { get; set; }

    /// <summary>Optimistic-concurrency token. See <c>ProviderSpecificMapping</c> for the Postgres mapping.</summary>
    /// <remarks>Nullable like the other tokens: SQL Server generates it, but SQLite (used in tests) cannot, so the column must accept a null on insert.</remarks>
    [Timestamp]
    public byte[]? RowVersion { get; set; }

    /// <summary>The task this plan rolls out.</summary>
    [ForeignKey(nameof(TargetTaskId))]
    [DeleteBehavior(DeleteBehavior.Restrict)]
    public TaskItem TargetTask { get; set; } = null!;

    /// <summary>The tasks in the target's dependency closure that this plan covers.</summary>
    [InverseProperty(nameof(PlanTaskNode.RolloutPlan))]
    public ICollection<PlanTaskNode> Nodes { get; set; } = [];

    /// <summary>Operator edits, replayed on each recalculation.</summary>
    [InverseProperty(nameof(PlanOverride.RolloutPlan))]
    public ICollection<PlanOverride> Overrides { get; set; } = [];
}
