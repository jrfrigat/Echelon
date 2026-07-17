using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ReleaseOrchestrator.Application.Contracts.Messages;
using ReleaseOrchestrator.Infrastructure.Persistence.Models;
using ReleaseOrchestrator.Core.Parsing;
using ReleaseOrchestrator.Infrastructure.Persistence;

namespace ReleaseOrchestrator.Infrastructure.Queue.Consumers;

/// <summary>
/// Upserts an open merge request and derives its deployability from the connection's
/// ready-for-deploy label (README §5).
/// </summary>
public class MrOpenedConsumer(
    AppDbContext db,
    IPublishEndpoint publisher,
    TimeProvider clock,
    ILogger<MrOpenedConsumer> logger) : IConsumer<MrOpened>
{
    public async Task Consume(ConsumeContext<MrOpened> context)
    {
        var msg = context.Message;
        var ct = context.CancellationToken;

        var repo = await db.Repositories
            .Include(r => r.Connection)
            .Include(r => r.TrackerConnection)
            .FirstOrDefaultAsync(
                r => r.Connection.Name == msg.ConnectionName && r.ExternalId == msg.RepositoryExternalId,
                ct);

        if (repo is null)
        {
            // Not retryable: the repository is absent from configuration and redelivering
            // will not conjure it.
            logger.LogWarning(
                "Repository not found for connection={Connection}, path={Path}; ignoring MR {Mr}",
                msg.ConnectionName, msg.RepositoryExternalId, msg.ExternalMrId);
            return;
        }

        var taskId = await ResolveTaskIdAsync(repo, msg.TaskExternalId, msg.ExternalMrId, ct);

        var mr = await db.MergeRequests.FirstOrDefaultAsync(
            m => m.RepositoryId == repo.Id && m.ExternalId == msg.ExternalMrId, ct);

        var now = clock.GetUtcNow().UtcDateTime;

        if (mr is null)
        {
            mr = new MergeRequest
            {
                Id = Guid.NewGuid(),
                ExternalId = msg.ExternalMrId,
                RepositoryId = repo.Id,
                CreatedAt = now
            };
            db.MergeRequests.Add(mr);
        }

        mr.SourceBranch = msg.SourceBranch;
        mr.TargetBranch = msg.TargetBranch;
        // Recorded even when the task is unknown, so TaskSyncConsumer can attach this MR once
        // the task lands rather than leaving it unordered forever.
        mr.TaskExternalId = msg.TaskExternalId;
        if (taskId is not null) mr.TaskId = taskId;

        // A reopened MR must shed its terminal timestamps, or archiving still claims it.
        mr.MergedAt = null;
        mr.ClosedAt = null;
        mr.Status = MergeRequestStatusResolver.ResolveOpenStatus(
            msg.Labels, repo.Connection.ReadyForDeployLabel, mr.IsStatusManual, mr.Status);

        await db.SaveChangesAsync(ct);

        // Requested unconditionally, including when the MR already existed. Returning early
        // on "exists" meant a redelivery after a crash stored the MR but never replanned it,
        // and a label change never reached the plan at all.
        await publisher.Publish(
            new ReleasePlanRecalculationRequested(now, $"MR {msg.ExternalMrId} opened or updated"), ct);
    }

    /// <summary>
    /// Resolves a branch's issue key to a task, and asks for the task to be imported when it is
    /// not stored yet.
    ///
    /// Scoped to the repository's tracker when one is configured. Without that scope a key can
    /// only be matched globally, and when it is ambiguous across trackers we link nothing rather
    /// than pick arbitrarily: a missing link costs ordering, a wrong link corrupts it.
    /// </summary>
    private async Task<Guid?> ResolveTaskIdAsync(Repository repo, string? taskExternalId, string mrExternalId, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(taskExternalId)) return null;

        var query = db.Tasks.Where(t => t.ExternalId == taskExternalId);
        if (repo.TrackerConnectionId is { } trackerId)
            query = query.Where(t => t.TrackerConnectionId == trackerId);

        var candidates = await query
            .Select(t => new { t.Id, t.TrackerConnectionId })
            .Take(2)
            .ToListAsync(ct);

        if (candidates.Count == 1) return candidates[0].Id;

        if (candidates.Count > 1)
        {
            logger.LogWarning(
                "Task key {Task} exists in more than one tracker; leaving MR {Mr} unlinked rather than guessing. "
                + "Set the repository's tracker connection to resolve this.",
                taskExternalId, mrExternalId);
            return null;
        }

        // Unknown key. If the repository names its tracker, import the task instead of dropping
        // the link: the plan needs the task's own dependencies either way, and waiting for the
        // task's webhook would leave this MR unordered until something else happens to touch it.
        if (repo.TrackerConnection is { } tracker)
        {
            logger.LogInformation(
                "Task {Task} referenced by MR {Mr} is unknown; requesting a sync from tracker {Tracker}.",
                taskExternalId, mrExternalId, tracker.Name);

            await publisher.Publish(
                new TaskSyncRequested(tracker.Name, taskExternalId, $"Referenced by MR {mrExternalId}"), ct);
        }
        else
        {
            logger.LogInformation(
                "Task {Task} referenced by MR {Mr} is unknown and the repository has no tracker connection; "
                + "the MR stays unlinked.",
                taskExternalId, mrExternalId);
        }

        return null;
    }
}
