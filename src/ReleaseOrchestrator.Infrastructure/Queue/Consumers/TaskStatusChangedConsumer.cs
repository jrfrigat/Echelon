using MassTransit;
using Microsoft.EntityFrameworkCore;
using ReleaseOrchestrator.Application.Contracts.Messages;
using ReleaseOrchestrator.Infrastructure.Persistence;

namespace ReleaseOrchestrator.Infrastructure.Queue.Consumers;

public class TaskStatusChangedConsumer(AppDbContext db, IPublishEndpoint publisher) : IConsumer<TaskStatusChanged>
{
    private static readonly HashSet<string> ClosedStatuses = ["closed", "cancelled", "rejected", "resolved"];

    public async Task Consume(ConsumeContext<TaskStatusChanged> context)
    {
        var msg = context.Message;
        var task = await db.Tasks.FirstOrDefaultAsync(
            t => t.ExternalId == msg.ExternalId,
            context.CancellationToken);

        if (task is null) return;

        task.Status = msg.NewStatus;
        task.ClosedAt = msg.ClosedAt;

        await db.SaveChangesAsync(context.CancellationToken);

        if (ClosedStatuses.Contains(msg.NewStatus.ToLowerInvariant()))
            await publisher.Publish(new ReleasePlanRecalculationRequested(DateTime.UtcNow, "Task closed"), context.CancellationToken);
    }
}
