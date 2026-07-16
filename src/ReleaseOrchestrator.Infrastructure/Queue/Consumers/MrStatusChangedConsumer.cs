using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ReleaseOrchestrator.Application.Contracts.Messages;
using ReleaseOrchestrator.Core.Enums;
using ReleaseOrchestrator.Infrastructure.Persistence;
using ReleaseOrchestrator.Infrastructure.Queue;

namespace ReleaseOrchestrator.Infrastructure.Queue.Consumers;

public class MrStatusChangedConsumer(
    AppDbContext db,
    DebouncedRecalculationService debounce,
    ILogger<MrStatusChangedConsumer> logger) : IConsumer<MrStatusChanged>
{
    public async Task Consume(ConsumeContext<MrStatusChanged> context)
    {
        var msg = context.Message;

        var repo = await db.Repositories
            .Include(r => r.Connection)
            .FirstOrDefaultAsync(
                r => r.Connection.Name == msg.ConnectionName && r.ExternalId == msg.RepositoryExternalId,
                context.CancellationToken);

        if (repo is null)
        {
            logger.LogWarning("Repository not found for connection={Connection}, path={Path}", msg.ConnectionName, msg.RepositoryExternalId);
            return;
        }

        var mr = await db.MergeRequests.FirstOrDefaultAsync(
            m => m.RepositoryId == repo.Id && m.ExternalId == msg.ExternalMrId,
            context.CancellationToken);

        if (mr is null) return;

        mr.Status = msg.NewStatus;
        if (msg.NewStatus == MergeRequestStatus.Merged)
            mr.MergedAt = msg.ChangedAt;

        await db.SaveChangesAsync(context.CancellationToken);
        debounce.RequestRecalculation("MR status changed");
    }
}
