using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace ReleaseOrchestrator.Infrastructure.Persistence.Models;

/// <summary>An append-only record of one deploy attempt for a <see cref="RolloutStep"/>.</summary>
public class RolloutStepAttempt
{
    /// <summary>Primary key.</summary>
    [Key]
    public Guid Id { get; set; }

    /// <summary>The step this attempt belongs to.</summary>
    public Guid RolloutStepId { get; set; }

    /// <summary>The 1-based attempt number.</summary>
    public int AttemptNo { get; set; }

    /// <summary>The attempt outcome, e.g. Succeeded / Failed / Awaiting.</summary>
    [Required, MaxLength(50)]
    public string Outcome { get; set; } = string.Empty;

    /// <summary>An optional message (a failure reason, a pipeline reference).</summary>
    public string? Message { get; set; }

    /// <summary>When the attempt was recorded.</summary>
    public DateTime At { get; set; }

    /// <summary>The step. Cascade: attempts die with the run.</summary>
    [ForeignKey(nameof(RolloutStepId))]
    [InverseProperty(nameof(Models.RolloutStep.Attempts))]
    public RolloutStep RolloutStep { get; set; } = null!;
}
