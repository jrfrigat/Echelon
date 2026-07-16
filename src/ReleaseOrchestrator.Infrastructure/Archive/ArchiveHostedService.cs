using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ReleaseOrchestrator.Core.Enums;
using ReleaseOrchestrator.Infrastructure.Archive.Entities;
using ReleaseOrchestrator.Infrastructure.Persistence;
using System.Text.Json;

namespace ReleaseOrchestrator.Infrastructure.Archive;

public class ArchiveOptions
{
    public bool Enabled { get; set; } = true;
    public string ScheduleCron { get; set; } = "0 2 * * *";
    public int ArchiveAfterDays { get; set; } = 90;
    public int TaskBatchSize { get; set; } = 1000;
    public int MrBatchSize { get; set; } = 500;
}

public class ArchiveHostedService(
    IServiceScopeFactory scopeFactory,
    IOptions<ArchiveOptions> options,
    ILogger<ArchiveHostedService> logger) : BackgroundService
{
    private static readonly TimeSpan RunInterval = TimeSpan.FromHours(24);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled) return;

        while (!stoppingToken.IsCancellationRequested)
        {
            var now = DateTime.UtcNow;
            var nextRun = now.Date.AddDays(1).AddHours(2); // next 02:00 UTC
            var delay = nextRun - now;
            if (delay < TimeSpan.Zero) delay = RunInterval;

            await Task.Delay(delay, stoppingToken);
            await RunArchiveCycleAsync(stoppingToken);
        }
    }

    private async Task RunArchiveCycleAsync(CancellationToken ct)
    {
        logger.LogInformation("Archive cycle started");
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var archiveDb = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();

            var cutoff = DateTime.UtcNow.AddDays(-options.Value.ArchiveAfterDays);
            await ArchiveTasksAsync(db, archiveDb, cutoff, ct);
            await ArchiveMergeRequestsAsync(db, archiveDb, cutoff, ct);

            logger.LogInformation("Archive cycle completed");
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Archive cycle failed");
        }
    }

    private async Task ArchiveTasksAsync(AppDbContext db, ArchiveDbContext archiveDb, DateTime cutoff, CancellationToken ct)
    {
        while (true)
        {
            var tasks = await db.Tasks
                .Where(t => t.ClosedAt < cutoff
                    && !db.StageItems.Any(si => si.MergeRequest.TaskId == t.Id
                        && si.Stage.Plan.IsActive))
                .Take(options.Value.TaskBatchSize)
                .Include(t => t.Dependencies)
                .ToListAsync(ct);

            if (tasks.Count == 0) break;

            var archived = tasks.Select(t => new ArchivedTask
            {
                Id = t.Id,
                ExternalId = t.ExternalId,
                Title = t.Title,
                Status = t.Status,
                ClosedAt = t.ClosedAt,
                DependenciesJson = JsonSerializer.Serialize(t.Dependencies.Select(d => d.DependsOnTaskId)),
                ArchivedAt = DateTime.UtcNow
            }).ToList();

            await archiveDb.ArchivedTasks.AddRangeAsync(archived, ct);
            await archiveDb.SaveChangesAsync(ct);
            db.Tasks.RemoveRange(tasks);
            await db.SaveChangesAsync(ct);

            logger.LogInformation("Archived {Count} tasks", tasks.Count);
            await Task.Delay(TimeSpan.FromSeconds(1), ct);
        }
    }

    private async Task ArchiveMergeRequestsAsync(AppDbContext db, ArchiveDbContext archiveDb, DateTime cutoff, CancellationToken ct)
    {
        while (true)
        {
            var mrs = await db.MergeRequests
                .Where(mr => (mr.Status == MergeRequestStatus.Merged || mr.Status == MergeRequestStatus.Closed)
                    && mr.MergedAt < cutoff
                    && !db.StageItems.Any(si => si.MergeRequestId == mr.Id && si.Stage.Plan.IsActive))
                .Take(options.Value.MrBatchSize)
                .Include(mr => mr.Repository)
                .Include(mr => mr.Task)
                .ToListAsync(ct);

            if (mrs.Count == 0) break;

            var archived = mrs.Select(mr => new ArchivedMergeRequest
            {
                Id = mr.Id,
                ExternalId = mr.ExternalId,
                RepositoryName = mr.Repository.Name,
                SourceBranch = mr.SourceBranch,
                TargetBranch = mr.TargetBranch,
                Status = mr.Status.ToString(),
                TaskExternalId = mr.Task?.ExternalId,
                ClosedAt = mr.MergedAt,
                ArchivedAt = DateTime.UtcNow
            }).ToList();

            await archiveDb.ArchivedMergeRequests.AddRangeAsync(archived, ct);
            await archiveDb.SaveChangesAsync(ct);
            db.MergeRequests.RemoveRange(mrs);
            await db.SaveChangesAsync(ct);

            logger.LogInformation("Archived {Count} merge requests", mrs.Count);
            await Task.Delay(TimeSpan.FromSeconds(1), ct);
        }
    }
}
