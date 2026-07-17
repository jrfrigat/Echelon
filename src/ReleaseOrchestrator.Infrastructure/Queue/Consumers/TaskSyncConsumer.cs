using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Rebus.Bus;
using Rebus.Handlers;
using ReleaseOrchestrator.Application.Contracts.Messages;
using ReleaseOrchestrator.Application.Services;
using ReleaseOrchestrator.Infrastructure.Persistence;

namespace ReleaseOrchestrator.Infrastructure.Queue.Consumers;

/// <summary>
/// Pulls a task and its dependency links from the tracker.
///
/// This is the missing link that made the product not work: TrackerService.SyncTaskAsync
/// produces every TaskDependency row and nothing called it, so the table stayed empty, task
/// edges never existed, and the plan was ordered by stack links alone.
/// </summary>
public class TaskSyncConsumer(
    AppDbContext db,
    ITrackerService tracker,
    IBus bus,
    TimeProvider clock,
    ILogger<TaskSyncConsumer> logger) : IHandleMessages<TaskSyncRequested>
{
    /// <inheritdoc/>
    public async Task Handle(TaskSyncRequested message)
    {
        var msg = message;
        var ct = HandlerCancellation.Token;

        var connectionId = await db.TrackerConnections
            .Where(c => c.Name == msg.TrackerConnectionName)
            .Select(c => (Guid?)c.Id)
            .FirstOrDefaultAsync(ct);

        if (connectionId is null)
        {
            // Not retryable: the connection is absent from configuration.
            logger.LogWarning(
                "TrackerConnection {Name} not found; cannot sync {Task} ({Reason})",
                msg.TrackerConnectionName, msg.ExternalId, msg.Reason);
            return;
        }

        // Exceptions propagate: a tracker outage is transient, and retry plus scheduled
        // redelivery is what turns it into a delay rather than a lost dependency edge.
        var dependenciesChanged = await tracker.SyncTaskAsync(connectionId.Value, msg.ExternalId, ct);
        var linkedMergeRequests = await LinkWaitingMergeRequestsAsync(connectionId.Value, msg.ExternalId, ct);

        if (dependenciesChanged || linkedMergeRequests > 0)
            await bus.Send(new ReleasePlanRecalculationRequested(
                clock.GetUtcNow().UtcDateTime, $"Task {msg.ExternalId} synced"));
    }

    /// <summary>
    /// Attaches merge requests whose branch named this task before the task existed.
    ///
    /// Webhook order is not guaranteed — an MR commonly arrives before its task — and without
    /// this the MR stays unlinked for good: its own event has already been handled and nothing
    /// revisits it. Scoped to the tracker's own repositories so a same-named key in another
    /// tracker cannot capture them.
    /// </summary>
    private async Task<int> LinkWaitingMergeRequestsAsync(Guid trackerConnectionId, string externalId, CancellationToken ct)
    {
        var taskId = await db.Tasks
            .Where(t => t.TrackerConnectionId == trackerConnectionId && t.ExternalId == externalId)
            .Select(t => (Guid?)t.Id)
            .FirstOrDefaultAsync(ct);

        if (taskId is null) return 0;

        var linked = await db.MergeRequests
            .Where(mr => mr.TaskId == null
                         && mr.TaskExternalId == externalId
                         && mr.Repository.TrackerConnectionId == trackerConnectionId)
            .ExecuteUpdateAsync(s => s.SetProperty(mr => mr.TaskId, taskId), ct);

        if (linked > 0)
            logger.LogInformation("Linked {Count} merge request(s) to task {Task}", linked, externalId);

        return linked;
    }
}
