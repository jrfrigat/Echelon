using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;
using ReleaseOrchestrator.Core.Enums;

namespace ReleaseOrchestrator.Infrastructure.Persistence.Models;

/// <summary>
/// One rollout run: a target task deployed into one environment, from a frozen plan snapshot.
/// </summary>
/// <remarks>
/// All execution state lives in the database so a restarted coordinator resumes from rows, not
/// memory. <see cref="IdempotencyKey"/> is unique so a double-submit cannot launch two runs
/// (docs/issues/007-execution-engine.md).
/// </remarks>
[Index(nameof(IdempotencyKey), IsUnique = true, Name = "IX_Rollout_IdempotencyKey")]
[Index(nameof(TargetTaskId), Name = "IX_Rollout_TargetTaskId")]
public class Rollout
{
    /// <summary>Primary key.</summary>
    [Key]
    public Guid Id { get; set; }

    /// <summary>The task being rolled out.</summary>
    public Guid TargetTaskId { get; set; }

    /// <summary>The plan this run was materialised from.</summary>
    public Guid RolloutPlanId { get; set; }

    /// <summary>The target environment.</summary>
    public Guid EnvironmentId { get; set; }

    /// <summary>The plan frozen at launch, as JSON, so the run is unaffected by later plan edits.</summary>
    [Required]
    public string PlanSnapshotJson { get; set; } = string.Empty;

    /// <summary>Run lifecycle. See <see cref="RolloutStatus"/>.</summary>
    public RolloutStatus Status { get; set; }

    /// <summary>The oid of the operator who launched it.</summary>
    [MaxLength(64)]
    public string? LaunchedByOid { get; set; }

    /// <summary>Deterministic key that makes launching idempotent; unique.</summary>
    [Required, MaxLength(200)]
    public string IdempotencyKey { get; set; } = string.Empty;

    /// <summary>When the run started.</summary>
    public DateTime StartedAt { get; set; }

    /// <summary>When it reached a terminal status.</summary>
    public DateTime? FinishedAt { get; set; }

    /// <summary>Optimistic-concurrency token. See <c>ProviderSpecificMapping</c> for the Postgres mapping.</summary>
    [Timestamp]
    public byte[]? RowVersion { get; set; }

    /// <summary>The target task. Restrict.</summary>
    [ForeignKey(nameof(TargetTaskId))]
    [DeleteBehavior(DeleteBehavior.Restrict)]
    public TaskItem TargetTask { get; set; } = null!;

    /// <summary>The plan. Restrict: a run keeps its plan reference even after a replan.</summary>
    [ForeignKey(nameof(RolloutPlanId))]
    [DeleteBehavior(DeleteBehavior.Restrict)]
    public RolloutPlan RolloutPlan { get; set; } = null!;

    /// <summary>The environment. Restrict.</summary>
    [ForeignKey(nameof(EnvironmentId))]
    [DeleteBehavior(DeleteBehavior.Restrict)]
    public DeploymentEnvironment Environment { get; set; } = null!;

    /// <summary>The per-merge-request steps of this run.</summary>
    [InverseProperty(nameof(RolloutStep.Rollout))]
    public ICollection<RolloutStep> Steps { get; set; } = [];
}
