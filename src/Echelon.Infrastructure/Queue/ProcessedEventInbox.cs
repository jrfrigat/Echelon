using Microsoft.EntityFrameworkCore;
using Echelon.Infrastructure.Persistence;
using Echelon.Infrastructure.Persistence.Models;

namespace Echelon.Infrastructure.Queue;

/// <summary>
/// The dedup inbox: a durable record of the events whose handling ran to completion.
/// </summary>
/// <remarks>
/// The mark is written AFTER the handler succeeds, not before (<see cref="EventDedupStep"/>): a
/// handler that throws to be retried -- the out-of-order recovery a status event uses when its
/// opened event has not landed yet -- must be redelivered, and it can only be redelivered if it was
/// never recorded as processed. Recording before handling turned a retryable failure into a lost
/// event. The cost is at-least-once delivery for the window between a handler succeeding and the
/// mark committing; every ingestion handler is idempotent, so a re-run in that window is harmless.
///
/// The composite primary key on (Source, EventId) makes a concurrent double-mark a unique violation,
/// caught and read as "already recorded" -- but only after confirming the row is actually present,
/// so a transient database failure is rethrown for retry rather than silently swallowed.
/// </remarks>
public class ProcessedEventInbox(AppDbContext db, TimeProvider clock)
{
    /// <summary>True when this event's handling has already been recorded as complete.</summary>
    /// <param name="source">The event's source.</param>
    /// <param name="eventId">The event's id, unique within the source.</param>
    /// <param name="ct">Cancellation token.</param>
    public Task<bool> IsProcessedAsync(string source, string eventId, CancellationToken ct) =>
        db.ProcessedEvents.AsNoTracking()
            .AnyAsync(e => e.Source == source && e.EventId == eventId, ct);

    /// <summary>
    /// Records that this event has been handled to completion. Idempotent: a concurrent delivery
    /// that recorded it first is not an error. A transient database failure IS -- it propagates so
    /// the (idempotent) handler is retried and the mark reattempted, rather than being mistaken for
    /// a duplicate and dropped.
    /// </summary>
    /// <param name="source">The event's source.</param>
    /// <param name="eventId">The event's id, unique within the source.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task MarkProcessedAsync(string source, string eventId, CancellationToken ct)
    {
        db.ProcessedEvents.Add(new ProcessedEvent
        {
            Source = source,
            EventId = eventId,
            ProcessedAt = clock.GetUtcNow().UtcDateTime
        });

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            foreach (var entry in db.ChangeTracker.Entries<ProcessedEvent>()
                         .Where(e => e.State == EntityState.Added).ToList())
                entry.State = EntityState.Detached;

            // Only a genuine duplicate is benign. If the row is not actually present the insert
            // failed for another reason (deadlock, timeout) -- rethrow so it is retried, not dropped.
            if (!await IsProcessedAsync(source, eventId, ct)) throw;
        }
    }
}
