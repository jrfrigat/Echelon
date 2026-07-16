using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ReleaseOrchestrator.Application.Contracts.Messages;
using ReleaseOrchestrator.Core.Entities;
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

        var taskId = await ResolveTaskIdAsync(msg.TaskExternalId, msg.ExternalMrId, ct);

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
    /// Resolves a branch's issue key to a task.
    ///
    /// Repositories hang off a VCS connection and tasks off a tracker connection, with no
    /// link between them in the model (README defers TrackerProject), so a key can only be
    /// matched globally. When it is ambiguous across trackers we link nothing rather than
    /// pick arbitrarily: a missing link costs ordering, a wrong link corrupts it.
    /// </summary>
    private async Task<Guid?> ResolveTaskIdAsync(string? taskExternalId, string mrExternalId, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(taskExternalId)) return null;

        var candidates = await db.Tasks
            .Where(t => t.ExternalId == taskExternalId)
            .Select(t => new { t.Id, t.TrackerConnectionId })
            .Take(2)
            .ToListAsync(ct);

        switch (candidates.Count)
        {
            case 1:
                return candidates[0].Id;
            case 0:
                logger.LogInformation(
                    "Task {Task} referenced by MR {Mr} is not known yet; linking deferred to VCS sync.",
                    taskExternalId, mrExternalId);
                return null;
            default:
                logger.LogWarning(
                    "Task key {Task} exists in more than one tracker; leaving MR {Mr} unlinked rather than "
                    + "guessing. Give the repository an unambiguous tracker to resolve this.",
                    taskExternalId, mrExternalId);
                return null;
        }
    }
}
