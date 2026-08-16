using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;

namespace Echelon.Infrastructure.Persistence.Models;

/// <summary>
/// The dedup inbox: one row per ingestion event that has already been handled.
/// </summary>
/// <remarks>
/// Checked in a Rebus incoming step before dispatch; a composite-key conflict means the event was
/// already processed, so it is acked and dropped. Handler-level upsert idempotency alone is not
/// enough once push and poll can both observe the same change.
/// The key columns are bounded so the composite primary key fits SQL Server's index-key size limit.
/// </remarks>
[PrimaryKey(nameof(Source), nameof(EventId))]
public class ProcessedEvent
{
    /// <summary>The producer, part of the composite key.</summary>
    [Required, MaxLength(200)]
    public string Source { get; set; } = string.Empty;

    /// <summary>The event id, part of the composite key.</summary>
    [Required, MaxLength(200)]
    public string EventId { get; set; } = string.Empty;

    /// <summary>When it was first processed.</summary>
    public DateTime ProcessedAt { get; set; }
}
