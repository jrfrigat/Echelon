using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace ReleaseOrchestrator.Infrastructure.Persistence.Models;

/// <summary>An append-only audit-timeline entry for a <see cref="Rollout"/>.</summary>
public class RolloutEvent
{
    /// <summary>Primary key.</summary>
    [Key]
    public Guid Id { get; set; }

    /// <summary>The run this event belongs to.</summary>
    public Guid RolloutId { get; set; }

    /// <summary>The event kind, e.g. Launched / StepSucceeded / Paused / Cancelled.</summary>
    [Required, MaxLength(100)]
    public string Kind { get; set; } = string.Empty;

    /// <summary>Optional structured payload as JSON.</summary>
    public string? PayloadJson { get; set; }

    /// <summary>When the event occurred.</summary>
    public DateTime At { get; set; }

    /// <summary>The run. Cascade: the timeline dies with the run.</summary>
    [ForeignKey(nameof(RolloutId))]
    [DeleteBehavior(DeleteBehavior.Cascade)]
    public Rollout Rollout { get; set; } = null!;
}
