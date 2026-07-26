using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Rebus.Bus;
using Rebus.Handlers;
using ReleaseOrchestrator.Application.Contracts.Messages;
using ReleaseOrchestrator.Application.DTOs;
using ReleaseOrchestrator.Core.Enums;
using ReleaseOrchestrator.Infrastructure.Audit;
using ReleaseOrchestrator.Core.Parsing;
using ReleaseOrchestrator.Infrastructure.Persistence;
using ReleaseOrchestrator.Infrastructure.Persistence.Models;
using ReleaseOrchestrator.Providers.Abstractions;
using ReleaseOrchestrator.Providers.Abstractions.Vcs;

namespace ReleaseOrchestrator.Infrastructure.Queue.Consumers;

/// <summary>
/// Upserts an open merge request and derives its deployability from the connection's
/// ready-for-deploy label (README §5).
/// </summary>
public class MrOpenedConsumer(
    AppDbContext db,
    IBus bus,
    TimeProvider clock,
    ILogger<MrOpenedConsumer> logger) : IHandleMessages<MrOpened>
{
    /// <inheritdoc/>
    public async Task Handle(MrOpened message)
    {
        var msg = message;
        var ct = HandlerCancellation.Token;

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

        // Which field the task key comes from, and by what pattern, is the connection's rule. The
        // parser could not apply it -- it runs in the ingress with no connection -- so the raw
        // candidates (branch, title, labels) travelled and the key is resolved here.
        var (source, pattern) = TaskLinkSettings.RuleFrom(
            ProviderSettingsBag.Deserialize(repo.Connection.ProviderSettingsJson));
        var taskExternalId = TaskKeyExtractor.Extract(source, pattern, msg.SourceBranch, msg.Title, msg.Labels);

        var taskId = await ResolveTaskIdAsync(repo, taskExternalId, msg.ExternalMrId, ct);

        var mr = await db.MergeRequests.FirstOrDefaultAsync(
            m => m.RepositoryId == repo.Id && m.ExternalId == msg.ExternalMrId, ct);

        var now = clock.GetUtcNow().UtcDateTime;

        var isNew = mr is null;
        if (isNew)
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

        mr!.SourceBranch = msg.SourceBranch;
        mr.TargetBranch = msg.TargetBranch;
        // Recorded even when the task is unknown, so TaskSyncConsumer can attach this MR once
        // the task lands rather than leaving it unordered forever.
        mr.TaskExternalId = taskExternalId;
        if (taskId is not null) mr.TaskId = taskId;

        // A merge is final: GitLab cannot reopen a merged merge request, so an 'opened' delivery for
        // one we already hold as Merged is a stale, out-of-order event from before the merge (deliveries
        // are unordered across workers). Honouring it would resurrect a deployed MR back into the plan
        // and back into archiving eligibility. A CLOSED MR, by contrast, can genuinely be reopened, so
        // that still sheds its terminal timestamps and rejoins the plan; a brand-new MR always resolves.
        if (isNew || mr.Status != MergeRequestStatus.Merged)
        {
            mr.MergedAt = null;
            mr.ClosedAt = null;

            var previousStatus = isNew ? (MergeRequestStatus?)null : mr.Status;
            mr.Status = MergeRequestStatusResolver.ResolveOpenStatus(
                msg.Labels, repo.Connection.ReadyForDeployLabel, mr.IsStatusManual, mr.Status);

            // The label-driven promotion to ReadyForDeploy happens here and nowhere else -- the
            // coarse "is this deployable" signal from the connection's single ready-for-deploy label,
            // distinct from the per-environment readiness gate below. It overwrites the column in
            // place, so without this row nothing afterwards can say when it became deployable.
            MergeRequestStatusJournal.Record(
                db, mr, previousStatus, mr.Status,
                MergeRequestStatusJournal.CauseLabel, ActorRef.System, clock.GetUtcNow().UtcDateTime);

            // Persist the full label set the per-environment readiness gate consults, separate from
            // the single ready-for-deploy label the status above turns on. GitLab re-sends an opened
            // event on every label change, so this is where the stored set is kept current; the
            // writer no-ops when nothing changed. Inside the same guard as the status update, so a
            // stale opened event for an already-merged MR does not rewrite its labels either.
            MergeRequestLabelJournal.Apply(
                db, mr, msg.Labels, MergeRequestLabelJournal.CauseWebhook, ActorRef.System, now);
        }
        else
        {
            logger.LogInformation(
                "Ignoring opened event for merged MR {Mr}: a merge is final, so this is a stale delivery.",
                msg.ExternalMrId);
        }

        await db.SaveChangesAsync(ct);

        // Requested unconditionally, including when the MR already existed. Returning early
        // on "exists" meant a redelivery after a crash stored the MR but never replanned it,
        // and a label change never reached the plan at all.
        await bus.Send(
            new ReleasePlanRecalculationRequested(now, $"MR {msg.ExternalMrId} opened or updated"));
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

            await bus.Send(
                new TaskSyncRequested(tracker.Name, taskExternalId, $"Referenced by MR {mrExternalId}"));
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
