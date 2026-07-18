using Microsoft.EntityFrameworkCore;
using ReleaseOrchestrator.Infrastructure.Persistence;
using ReleaseOrchestrator.Infrastructure.Persistence.Models;

namespace ReleaseOrchestrator.Infrastructure.Queue;

/// <summary>
/// The dedup inbox: records that an event has been processed, and reports whether it is new.
/// </summary>
/// <remarks>
/// The insert is the check: the composite primary key on (Source, EventId) makes a repeat a unique
/// violation, which is caught and read as "already processed". No read-then-write race -- two
/// concurrent workers both attempt the insert and exactly one wins.
/// </remarks>
public class ProcessedEventInbox(AppDbContext db, TimeProvider clock)
{
    /// <summary>
    /// Records the event as processed. Returns <c>true</c> when it was new (proceed to handle it),
    /// <c>false</c> when it was already recorded (a duplicate to drop).
    /// </summary>
    /// <param name="source">The event's source.</param>
    /// <param name="eventId">The event's id, unique within the source.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<bool> TryMarkProcessedAsync(string source, string eventId, CancellationToken ct)
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
            return true;
        }
        catch (DbUpdateException)
        {
            // Composite-key conflict: another delivery of this event was processed first.
            foreach (var entry in db.ChangeTracker.Entries<ProcessedEvent>()
                         .Where(e => e.State == EntityState.Added).ToList())
                entry.State = EntityState.Detached;
            return false;
        }
    }
}
