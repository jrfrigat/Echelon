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
///.
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

    /// <summary>
    /// The kind of actor that launched it (see <c>ActorKinds</c>): a person, a service principal, or
    /// a machine path.
    /// </summary>
    /// <remarks>
    /// Separate from the oid because a null oid alone is ambiguous - it is equally true of a
    /// background trigger and of a signed-in operator whose token carried no usable object id. A CI
    /// pipeline's client-credential token in particular resolves an object id like anyone else, and
    /// nothing here validates scopes, so without this a pipeline deploy reads as a person.
    /// </remarks>
    [MaxLength(32)]
    public string? LaunchedByKind { get; set; }

    /// <summary>
    /// The launcher's display name as their token spelled it.
    /// </summary>
    /// <remarks>
    /// Captured at launch because it cannot be looked up afterwards: this context derives from
    /// <c>IdentityDbContext</c> but nothing ever creates a user, so <c>AspNetUsers</c> is empty and
    /// an oid recorded without a name stays a raw GUID forever.
    /// </remarks>
    [MaxLength(200)]
    public string? LaunchedByName { get; set; }

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
