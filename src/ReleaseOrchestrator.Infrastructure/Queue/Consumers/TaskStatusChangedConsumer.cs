using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ReleaseOrchestrator.Application.Contracts.Messages;
using ReleaseOrchestrator.Core.Parsing;
using ReleaseOrchestrator.Infrastructure.Persistence;

namespace ReleaseOrchestrator.Infrastructure.Queue.Consumers;

public class TaskStatusChangedConsumer(
    AppDbContext db,
    IPublishEndpoint publisher,
    TimeProvider clock,
    ILogger<TaskStatusChangedConsumer> logger) : IConsumer<TaskStatusChanged>
{
    public async Task Consume(ConsumeContext<TaskStatusChanged> context)
    {
        var msg = context.Message;
        var ct = context.CancellationToken;

        var conn = await db.TrackerConnections
            .FirstOrDefaultAsync(c => c.Name == msg.TrackerConnectionName, ct);

        if (conn is null)
        {
            logger.LogWarning(
                "TrackerConnection {Name} not found; ignoring status change for {Task}",
                msg.TrackerConnectionName, msg.ExternalId);
            return;
        }

        // Scoped by connection: ExternalId is unique only within a tracker, so matching on it
        // alone would stamp the status onto a same-keyed task in a different tracker.
        var task = await db.Tasks.FirstOrDefaultAsync(
            t => t.TrackerConnectionId == conn.Id && t.ExternalId == msg.ExternalId, ct);

        if (task is null)
            // Retry: the created event is likely still in flight.
            throw new TaskNotYetKnownException(
                $"Task {msg.ExternalId} in tracker {msg.TrackerConnectionName} is not known yet; "
                + "retrying so the created event can land first.");

        var wasClosed = task.ClosedAt is not null;

        task.Status = msg.NewStatus;
        // Derived here rather than taken from the message, so the closed-status rule has one
        // owner: the ingress and this consumer used to keep lists that disagreed on
        // "resolved", which left such tasks closed-but-unarchivable forever.
        task.ClosedAt = TaskStatusRules.IsClosed(msg.NewStatus)
            ? msg.ClosedAt ?? clock.GetUtcNow().UtcDateTime
            : null;

        await db.SaveChangesAsync(ct);

        // Any crossing of the closed boundary changes which MRs are deployable — reopening
        // matters as much as closing.
        if (wasClosed != (task.ClosedAt is not null))
            await publisher.Publish(new ReleasePlanRecalculationRequested(
                clock.GetUtcNow().UtcDateTime, $"Task {msg.ExternalId} status changed to {msg.NewStatus}"), ct);
    }
}

/// <summary>Signals a transient ordering gap between tracker events, so retry resolves it.</summary>
public class TaskNotYetKnownException(string message) : Exception(message);
