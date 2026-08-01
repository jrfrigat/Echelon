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

namespace ReleaseOrchestrator.Infrastructure.Queue.Consumers;

/// <summary>
/// Applies a merge request's status transition, and the terminal timestamps archiving depends on.
/// </summary>
/// <remarks>
/// Deliveries are unordered, which shapes both of the unusual decisions here: an unknown merge
/// request throws so retry can let its opened event land first, and a non-terminal status for an
/// already-terminal merge request is dropped rather than applied, since honouring it would
/// resurrect a merged request back into the plan.
/// </remarks>
public class MrStatusChangedConsumer(
    AppDbContext db,
    IBus bus,
    TimeProvider clock,
    ILogger<MrStatusChangedConsumer> logger) : IHandleMessages<MrStatusChanged>
{
    /// <inheritdoc/>
    public async Task Handle(MrStatusChanged message)
    {
        var msg = message;
        var ct = HandlerCancellation.Token;

        var repo = await db.Repositories
            .Include(r => r.Connection)
            .FirstOrDefaultAsync(
                r => r.Connection.Name == msg.ConnectionName && r.ExternalId == msg.RepositoryExternalId, ct);

        if (repo is null)
        {
            logger.LogWarning(
                "Repository not found for connection={Connection}, path={Path}; ignoring status change for MR {Mr}",
                msg.ConnectionName, msg.RepositoryExternalId, msg.ExternalMrId);
            return;
        }

        var mr = await db.MergeRequests.FirstOrDefaultAsync(
            m => m.RepositoryId == repo.Id && m.ExternalId == msg.ExternalMrId, ct);

        if (mr is null)
            // Throw rather than return: the opened event is probably still in flight, and
            // this is exactly what retry is for. Swallowing it discarded the merge event for
            // good, leaving a merged MR in the plan forever.
            throw new MergeRequestNotYetKnownException(
                $"MR {msg.ExternalMrId} in {msg.ConnectionName}/{msg.RepositoryExternalId} is not known yet; "
                + "retrying so the opened event can land first.");

        // Concurrent deliveries are unordered, so "opened" can be applied after "merged" and
        // resurrect a merged MR into the plan. Terminal states are final.
        if (MergeRequestStatusResolver.IsTerminal(mr.Status) && !MergeRequestStatusResolver.IsTerminal(msg.NewStatus))
        {
            logger.LogInformation(
                "Ignoring {NewStatus} for MR {Mr}: already terminal at {CurrentStatus}.",
                msg.NewStatus, msg.ExternalMrId, mr.Status);
            return;
        }

        // Captured before the assignment: after it there is nothing left that says what it was.
        var previousStatus = mr.Status;
        mr.Status = msg.NewStatus;

        // ChangedAt, not the local clock: this is the VCS's own account of when it happened, and it
        // is what the merged/closed timestamps below are already stamped with. Using two different
        // clocks for one event would put the journal entry and the timestamp it describes minutes
        // apart on the same timeline.
        MergeRequestStatusJournal.Record(
            db, mr, previousStatus, msg.NewStatus,
            MergeRequestStatusJournal.CauseWebhook, ActorRef.System, msg.ChangedAt);

        // A merge is the last moment labels are visible before the merged request leaves the
        // open-request listing, so record them here for the readiness gate. Only when the event
        // actually carries labels: an empty (or null) list means "no label information in this
        // event", not "labels were removed" -- treating it as removal would wipe the set captured
        // from the opened events. The null case is real: a message serialized before this field
        // existed deserializes with no labels, and must not fault the handler.
        if (msg.Labels is { Count: > 0 })
            MergeRequestLabelJournal.Apply(
                db, mr, msg.Labels, MergeRequestLabelJournal.CauseWebhook, ActorRef.System, msg.ChangedAt);

        // A terminal state is reported by the VCS and overrides any manual pin.
        if (MergeRequestStatusResolver.IsTerminal(msg.NewStatus))
            mr.IsStatusManual = false;

        // Distinct timestamps: a closed MR never gets a merge time, so archiving needs this
        // one set or the row stays ineligible forever.
        if (msg.NewStatus == MergeRequestStatus.Merged) mr.MergedAt = msg.ChangedAt;
        if (msg.NewStatus == MergeRequestStatus.Closed) mr.ClosedAt = msg.ChangedAt;

        await db.SaveChangesAsync(ct);

        await bus.Send(new ReleasePlanRecalculationRequested(
            clock.GetUtcNow().UtcDateTime, $"MR {msg.ExternalMrId} status changed to {msg.NewStatus}"));
    }
}

/// <summary>Signals a transient ordering gap between webhook events, so retry resolves it.</summary>
public class MergeRequestNotYetKnownException(string message) : Exception(message);
