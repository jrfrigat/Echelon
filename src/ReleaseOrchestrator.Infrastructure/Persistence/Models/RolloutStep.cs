using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;
using ReleaseOrchestrator.Core.Enums;

namespace ReleaseOrchestrator.Infrastructure.Persistence.Models;

/// <summary>One merge request's deploy within a <see cref="Rollout"/>, in the run's environment.</summary>
/// <remarks>
/// <see cref="Wave"/> is the merge-request-level Kahn level from the pure graph; a task's merge
/// requests can occupy different waves. The coordinator re-derives the ready frontier from these
/// rows, so it holds no state in memory.
/// </remarks>
[Index(nameof(RolloutId), nameof(MergeRequestId), IsUnique = true, Name = "IX_RolloutStep_Rollout_Mr")]
public class RolloutStep
{
    /// <summary>Primary key.</summary>
    [Key]
    public Guid Id { get; set; }

    /// <summary>The run this step belongs to.</summary>
    public Guid RolloutId { get; set; }

    /// <summary>The merge request this step deploys.</summary>
    public Guid MergeRequestId { get; set; }

    /// <summary>The merge request's task (for grouping and readiness).</summary>
    public Guid TaskId { get; set; }

    /// <summary>The execution wave (merge-request-level topological level).</summary>
    public int Wave { get; set; }

    /// <summary>The deploy strategy key resolved at launch (per-item override, per-environment target, or repository default).</summary>
    [Required, MaxLength(100)]
    public string DeployStrategyKey { get; set; } = string.Empty;

    /// <summary>
    /// The strategy settings frozen at launch, as JSON, with any secret keys still encrypted.
    /// </summary>
    /// <remarks>
    /// Frozen onto the step, not re-read from the repository at dispatch, so a configuration change
    /// made while a rollout is in flight cannot alter how its remaining steps deploy - the run
    /// executes the configuration it launched with. The strategy key was already frozen for this
    /// reason; the settings were not, and were re-read live, which this closes.
    /// </remarks>
    public string? DeploySettingsJson { get; set; }

    /// <summary>Step lifecycle. See <see cref="RolloutStepState"/>.</summary>
    public RolloutStepState State { get; set; }

    /// <summary>How many times the deploy has been attempted.</summary>
    public int AttemptCount { get; set; }

    /// <summary>The provider's handle for an async deploy (e.g. a pipeline id), used to reconcile on resume.</summary>
    [MaxLength(500)]
    public string? ExternalRef { get; set; }

    /// <summary>The merge request head sha at snapshot time, to detect drift before deploying.</summary>
    [MaxLength(100)]
    public string? MergeShaAtSnapshot { get; set; }

    /// <summary>The last failure message, if any.</summary>
    public string? LastError { get; set; }

    /// <summary>When the step started deploying.</summary>
    public DateTime? StartedAt { get; set; }

    /// <summary>When the step reached a terminal state.</summary>
    public DateTime? FinishedAt { get; set; }

    /// <summary>Optimistic-concurrency token. See <c>ProviderSpecificMapping</c> for the Postgres mapping.</summary>
    [Timestamp]
    public byte[]? RowVersion { get; set; }

    /// <summary>The run. Cascade: a step means nothing without its run.</summary>
    [ForeignKey(nameof(RolloutId))]
    [InverseProperty(nameof(Models.Rollout.Steps))]
    public Rollout Rollout { get; set; } = null!;

    /// <summary>The merge request. Restrict.</summary>
    [ForeignKey(nameof(MergeRequestId))]
    [DeleteBehavior(DeleteBehavior.Restrict)]
    public MergeRequest MergeRequest { get; set; } = null!;

    /// <summary>The task. Restrict.</summary>
    [ForeignKey(nameof(TaskId))]
    [DeleteBehavior(DeleteBehavior.Restrict)]
    public TaskItem Task { get; set; } = null!;

    /// <summary>Append-only attempt log.</summary>
    [InverseProperty(nameof(RolloutStepAttempt.RolloutStep))]
    public ICollection<RolloutStepAttempt> Attempts { get; set; } = [];
}
