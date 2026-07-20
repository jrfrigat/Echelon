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

    /// <summary>
    /// The event kind. The vocabulary is <c>RolloutEventKinds</c> — write a constant from there, not
    /// a literal.
    /// </summary>
    [Required, MaxLength(100)]
    public string Kind { get; set; } = string.Empty;

    /// <summary>Optional structured payload as JSON.</summary>
    public string? PayloadJson { get; set; }

    /// <summary>When the event occurred.</summary>
    public DateTime At { get; set; }

    /// <summary>
    /// Who caused it: the operator's object id, or null for anything the coordinator did on its own.
    /// </summary>
    /// <remarks>
    /// This is the only place a cancel, retry or skip can be attributed. Those three change state
    /// that carries no author — a retry resets the step to Pending and clears the error, leaving
    /// nothing to read afterwards — so an event not written here is an operator action that is
    /// permanently anonymous.
    /// </remarks>
    [MaxLength(64)]
    public string? ActorOid { get; set; }

    /// <summary>The kind of actor (see <c>ActorKinds</c>). Distinguishes a machine from a person we could not name.</summary>
    [MaxLength(32)]
    public string? ActorKind { get; set; }

    /// <summary>The actor's display name as their token spelled it, captured because it cannot be resolved later.</summary>
    [MaxLength(200)]
    public string? ActorName { get; set; }

    /// <summary>The run. Cascade: the timeline dies with the run.</summary>
    [ForeignKey(nameof(RolloutId))]
    [DeleteBehavior(DeleteBehavior.Cascade)]
    public Rollout Rollout { get; set; } = null!;
}
