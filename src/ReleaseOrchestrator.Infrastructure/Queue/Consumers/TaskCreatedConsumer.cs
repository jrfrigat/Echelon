using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ReleaseOrchestrator.Application.Contracts.Messages;
using ReleaseOrchestrator.Core.Entities;
using ReleaseOrchestrator.Infrastructure.Persistence;

namespace ReleaseOrchestrator.Infrastructure.Queue.Consumers;

public class TaskCreatedConsumer(AppDbContext db, ILogger<TaskCreatedConsumer> logger) : IConsumer<TaskCreated>
{
    public async Task Consume(ConsumeContext<TaskCreated> context)
    {
        var msg = context.Message;

        var conn = await db.TrackerConnections
            .FirstOrDefaultAsync(c => c.Name == msg.TrackerConnectionName, context.CancellationToken);

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
            context.CancellationToken);

        if (task is null)
        {
            db.Tasks.Add(new TaskItem
            {
                Id = Guid.NewGuid(),
                ExternalId = msg.ExternalId,
                Title = msg.Title,
                Status = "open",
                TrackerConnectionId = conn.Id
            });
        }
        else
        {
            task.Title = msg.Title;
        }

        await db.SaveChangesAsync(context.CancellationToken);
    }
}
