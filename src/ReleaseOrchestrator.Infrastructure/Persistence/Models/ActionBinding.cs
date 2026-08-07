using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;

namespace ReleaseOrchestrator.Infrastructure.Persistence.Models;

/// <summary>
/// Wires an event to an action: "when <see cref="EventType"/> fires, run <see cref="ActionType"/>
/// with <see cref="SettingsJson"/>".
/// </summary>
/// <remarks>
/// A binding is configuration, so a new event-to-action wiring is a row, and a new action TYPE is a
/// new handler class -- neither is a core change.
/// </remarks>
[Index(nameof(EventType), Name = "IX_ActionBinding_EventType")]
public class ActionBinding
{
    /// <summary>Primary key.</summary>
    [Key]
    public Guid Id { get; set; }

    /// <summary>The event that triggers the action, e.g. <c>RolloutSucceeded</c> or <c>TaskStatusChanged</c>.</summary>
    [Required, MaxLength(100)]
    public string EventType { get; set; } = string.Empty;

    /// <summary>The action-handler key, e.g. <c>telegram</c> or <c>tracker-status</c>.</summary>
    [Required, MaxLength(100)]
    public string ActionType { get; set; } = string.Empty;

    /// <summary>
    /// An optional scope filter, e.g. <c>env:prod</c> or <c>task:PROJ-1</c>. Null means every event of
    /// the type. The dispatcher matches it against the event payload.
    /// </summary>
    [MaxLength(200)]
    public string? Scope { get; set; }

    /// <summary>The action's settings as JSON (schema-declared by the handler).</summary>
    public string? SettingsJson { get; set; }

    /// <summary>Order among bindings that match the same event.</summary>
    public int Order { get; set; }

    /// <summary>Whether the binding is active.</summary>
    public bool Enabled { get; set; } = true;
}
