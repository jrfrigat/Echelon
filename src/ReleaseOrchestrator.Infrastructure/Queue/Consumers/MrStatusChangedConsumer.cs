using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ReleaseOrchestrator.Application.Contracts.Messages;
using ReleaseOrchestrator.Core.Enums;
using ReleaseOrchestrator.Core.Parsing;
using ReleaseOrchestrator.Infrastructure.Persistence;

namespace ReleaseOrchestrator.Infrastructure.Queue.Consumers;

public class MrStatusChangedConsumer(
    AppDbContext db,
    IPublishEndpoint publisher,
    TimeProvider clock,
    ILogger<MrStatusChangedConsumer> logger) : IConsumer<MrStatusChanged>
{
    public async Task Consume(ConsumeContext<MrStatusChanged> context)
    {
        var msg = context.Message;
        var ct = context.CancellationToken;

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

        mr.Status = msg.NewStatus;

        // A terminal state is reported by the VCS and overrides any manual pin.
        if (MergeRequestStatusResolver.IsTerminal(msg.NewStatus))
            mr.IsStatusManual = false;

        // Distinct timestamps: a closed MR never gets a merge time, so archiving needs this
        // one set or the row stays ineligible forever.
        if (msg.NewStatus == MergeRequestStatus.Merged) mr.MergedAt = msg.ChangedAt;
        if (msg.NewStatus == MergeRequestStatus.Closed) mr.ClosedAt = msg.ChangedAt;

        await db.SaveChangesAsync(ct);

        await publisher.Publish(new ReleasePlanRecalculationRequested(
            clock.GetUtcNow().UtcDateTime, $"MR {msg.ExternalMrId} status changed to {msg.NewStatus}"), ct);
    }
}

/// <summary>Signals a transient ordering gap between webhook events, so retry resolves it.</summary>
public class MergeRequestNotYetKnownException(string message) : Exception(message);
