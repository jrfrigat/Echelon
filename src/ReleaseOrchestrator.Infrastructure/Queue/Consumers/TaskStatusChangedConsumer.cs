using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Rebus.Bus;
using Rebus.Handlers;
using ReleaseOrchestrator.Application.Contracts.Messages;
using ReleaseOrchestrator.Infrastructure.Persistence;
using ReleaseOrchestrator.Infrastructure.Providers;
using ReleaseOrchestrator.Providers.Abstractions.Tracker;

namespace ReleaseOrchestrator.Infrastructure.Queue.Consumers;

/// <summary>Applies a tracker status change to the local task, and replans when it matters.</summary>
public class TaskStatusChangedConsumer(
    AppDbContext db,
    ITrackerProviderFactory providerFactory,
    IBus bus,
    TimeProvider clock,
    ILogger<TaskStatusChangedConsumer> logger) : IHandleMessages<TaskStatusChanged>
{
    /// <inheritdoc/>
    public async Task Handle(TaskStatusChanged message)
    {
        var msg = message;
        var ct = HandlerCancellation.Token;

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

        // Asked of the connection's provider rather than of a list held here, so the
        // closed-status rule has exactly one owner per tracker: the ingress and this consumer
        // used to keep lists that disagreed on "resolved", which left such tasks
        // closed-but-unarchivable forever. The owner is now the adapter that knows the tracker's
        // vocabulary, and both paths ask it.
        var provider = await providerFactory.CreateAsync(conn.ToDescriptor(), ct);

        task.Status = msg.NewStatus;
        task.ClosedAt = provider.IsClosedStatus(msg.NewStatus)
            ? msg.ClosedAt ?? clock.GetUtcNow().UtcDateTime
            : null;

        await db.SaveChangesAsync(ct);

        // Any crossing of the closed boundary changes which MRs are deployable - reopening
        // matters as much as closing.
        if (wasClosed != (task.ClosedAt is not null))
            await bus.Send(new ReleasePlanRecalculationRequested(
                clock.GetUtcNow().UtcDateTime, $"Task {msg.ExternalId} status changed to {msg.NewStatus}"));

        // Links are not carried by the status webhook and trackers do not raise an event when one
        // changes, so any touch of the issue is the cheapest moment to re-read them.
        await bus.Send(
            new TaskSyncRequested(msg.TrackerConnectionName, msg.ExternalId, $"Task status changed to {msg.NewStatus}"));
    }
}

/// <summary>Signals a transient ordering gap between tracker events, so retry resolves it.</summary>
public class TaskNotYetKnownException(string message) : Exception(message);
