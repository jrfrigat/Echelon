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

        var exists = await db.Tasks.AnyAsync(
            t => t.TrackerConnectionId == conn.Id && t.ExternalId == msg.ExternalId,
            context.CancellationToken);
        if (exists) return;

        db.Tasks.Add(new TaskItem
        {
            Id = Guid.NewGuid(),
            ExternalId = msg.ExternalId,
            Title = msg.Title,
            Status = "Open",
            TrackerConnectionId = conn.Id
        });

        await db.SaveChangesAsync(context.CancellationToken);
    }
}
