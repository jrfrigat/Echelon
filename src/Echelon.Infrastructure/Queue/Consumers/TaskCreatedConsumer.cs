using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Rebus.Bus;
using Rebus.Handlers;
using Echelon.Application.Contracts.Messages;
using Echelon.Infrastructure.Persistence;
using Echelon.Infrastructure.Persistence.Models;

namespace Echelon.Infrastructure.Queue.Consumers;

/// <summary>
/// Upserts a task observed in a tracker, then asks for its links to be pulled.
/// </summary>
/// <remarks>
/// The observation carries the title and status but not the issue's links, and the links are the
/// only thing that orders a plan - so this always follows up with a <see cref="TaskSyncRequested"/>
/// rather than treating the event as complete on its own.
/// </remarks>
public class TaskCreatedConsumer(
    AppDbContext db,
    IBus bus,
    TimeProvider clock,
    ILogger<TaskCreatedConsumer> logger) : IHandleMessages<TaskCreated>
{
    /// <inheritdoc/>
    public async Task Handle(TaskCreated message)
    {
        var msg = message;
        var ct = HandlerCancellation.Token;

        var conn = await db.TrackerConnections
            .FirstOrDefaultAsync(c => c.Name == msg.TrackerConnectionName, ct);

        if (conn is null)
        {
            logger.LogWarning("TrackerConnection not found: {Name}", msg.TrackerConnectionName);
            return;
        }

        // Upsert rather than check-then-skip: at-least-once delivery and multiple replicas
        // make the check racy, and the unique index on (TrackerConnectionId, ExternalId)
        // now turns a lost race into a retryable violation instead of a duplicate task.
        var task = await db.Tasks.FirstOrDefaultAsync(
            t => t.TrackerConnectionId == conn.Id && t.ExternalId == msg.ExternalId,
            ct);

        if (task is null)
        {
            db.Tasks.Add(new TaskItem
            {
                Id = Guid.NewGuid(),
                ExternalId = msg.ExternalId,
                Title = msg.Title,
                Status = "open",
                TrackerConnectionId = conn.Id,
                // Stamped only on the insert branch. A redelivery lands on the update branch below
                // and leaves it alone, so at-least-once delivery cannot move a task's arrival time.
                FirstSeenAt = clock.GetUtcNow().UtcDateTime,
                FirstSeenSource = string.IsNullOrWhiteSpace(msg.Source) ? null : msg.Source
            });
        }
        else
        {
            task.Title = msg.Title;
        }

        await db.SaveChangesAsync(ct);

        // The webhook carries the title and status but not the issue's links, and the links are
        // the only thing that orders the plan - so they have to be pulled.
        await bus.Send(
            new TaskSyncRequested(msg.TrackerConnectionName, msg.ExternalId, "Task created"));
    }
}
