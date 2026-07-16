using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ReleaseOrchestrator.Application.Contracts.Messages;
using ReleaseOrchestrator.Core.Entities;
using ReleaseOrchestrator.Core.Enums;
using ReleaseOrchestrator.Infrastructure.Persistence;
using ReleaseOrchestrator.Infrastructure.Queue;

namespace ReleaseOrchestrator.Infrastructure.Queue.Consumers;

public class MrOpenedConsumer(
    AppDbContext db,
    DebouncedRecalculationService debounce,
    ILogger<MrOpenedConsumer> logger) : IConsumer<MrOpened>
{
    public async Task Consume(ConsumeContext<MrOpened> context)
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

        var exists = await db.MergeRequests.AnyAsync(
            mr => mr.RepositoryId == repo.Id && mr.ExternalId == msg.ExternalMrId,
            context.CancellationToken);
        if (exists) return;

        Guid? taskId = null;
        if (!string.IsNullOrEmpty(msg.TaskExternalId))
        {
            taskId = (await db.Tasks.FirstOrDefaultAsync(
                t => t.ExternalId == msg.TaskExternalId,
                context.CancellationToken))?.Id;
        }

        db.MergeRequests.Add(new MergeRequest
        {
            Id = Guid.NewGuid(),
            ExternalId = msg.ExternalMrId,
            RepositoryId = repo.Id,
            SourceBranch = msg.SourceBranch,
            TargetBranch = msg.TargetBranch,
            TaskId = taskId,
            Status = MergeRequestStatus.Opened,
            CreatedAt = DateTime.UtcNow
        });

        await db.SaveChangesAsync(context.CancellationToken);
        debounce.RequestRecalculation("MR opened");
    }
}
