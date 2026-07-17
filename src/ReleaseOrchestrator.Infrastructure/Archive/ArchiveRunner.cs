using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ReleaseOrchestrator.Infrastructure.Persistence.Models;
using ReleaseOrchestrator.Core.Enums;
using ReleaseOrchestrator.Infrastructure.Archive.Entities;
using ReleaseOrchestrator.Infrastructure.Persistence;
using System.Text.Json;

namespace ReleaseOrchestrator.Infrastructure.Archive;

/// <summary>
/// Moves cold rows out of the operational database and into the archive, one batch at a time.
/// Call the phases in the order documented on <see cref="ArchiveHostedService"/>.
/// </summary>
internal sealed class ArchiveRunner(
    AppDbContext db,
    ArchiveDbContext archiveDb,
    ArchiveOptions options,
    TimeProvider clock,
    ILogger logger)
{
    private const int MaxBatchAttempts = 3;
    private static readonly TimeSpan BatchPause = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan RetryBackoff = TimeSpan.FromSeconds(2);

    public Task ArchiveReleasePlansAsync(DateTime cutoff, CancellationToken ct) =>
        RunBatchLoopAsync("release plans", c => LoadPlanBatchAsync(cutoff, c), ArchivePlanBatchAsync, ct);

    public Task ArchiveMergeRequestsAsync(DateTime cutoff, CancellationToken ct) =>
        RunBatchLoopAsync("merge requests", c => LoadMergeRequestBatchAsync(cutoff, c), ArchiveMergeRequestBatchAsync, ct);

    public Task ArchiveTasksAsync(DateTime cutoff, CancellationToken ct) =>
        RunBatchLoopAsync("tasks", c => LoadTaskBatchAsync(cutoff, c), ArchiveTaskBatchAsync, ct);

    // ---- release plans -------------------------------------------------------------------

    private Task<List<ReleasePlan>> LoadPlanBatchAsync(DateTime cutoff, CancellationToken ct) =>
        db.ReleasePlans
            .AsNoTracking()
            .Where(p => !p.IsActive && p.CreatedAt < cutoff)
            .OrderBy(p => p.CreatedAt)
            .Take(options.PlanBatchSize)
            .Include(p => p.Stages).ThenInclude(s => s.Items).ThenInclude(i => i.MergeRequest).ThenInclude(mr => mr.Repository)
            .Include(p => p.Stages).ThenInclude(s => s.Items).ThenInclude(i => i.MergeRequest).ThenInclude(mr => mr.Task)
            .AsSplitQuery()
            .ToListAsync(ct);

    private async Task ArchivePlanBatchAsync(List<ReleasePlan> plans, CancellationToken ct)
    {
        var archived = plans.Select(p => new ArchivedReleasePlan
        {
            Id = p.Id,
            Name = p.Name,
            Version = p.Version,
            PlanJson = JsonSerializer.Serialize(PlanSnapshot.From(p)),
            CreatedAt = p.CreatedAt,
            ArchivedAt = clock.GetUtcNow().UtcDateTime
        }).ToList();

        var ids = plans.Select(p => p.Id).ToList();
        var existing = await archiveDb.ArchivedReleasePlans.AsNoTracking()
            .Where(a => ids.Contains(a.Id)).Select(a => a.Id).ToListAsync(ct);

        await InsertMissingAsync(archiveDb.ArchivedReleasePlans, archived, a => a.Id, [.. existing], ct);

        // ReleaseStage and StageItem cascade from the plan. Dropping the plan is the only way
        // to release the StageItem -> MergeRequest references, which are Restrict.
        await db.ReleasePlans.Where(p => ids.Contains(p.Id)).ExecuteDeleteAsync(ct);
    }

    // ---- merge requests ------------------------------------------------------------------

    private Task<List<MergeRequest>> LoadMergeRequestBatchAsync(DateTime cutoff, CancellationToken ct) =>
        db.MergeRequests
            .AsNoTracking()
            .Where(mr => ((mr.Status == MergeRequestStatus.Merged && mr.MergedAt < cutoff)
                    || (mr.Status == MergeRequestStatus.Closed && mr.ClosedAt < cutoff))
                // Every surviving StageItem blocks the delete, not just those of active plans:
                // historical plans keep their items until the plan phase clears them.
                && !db.StageItems.Any(si => si.MergeRequestId == mr.Id))
            .OrderBy(mr => mr.Id)
            .Take(options.MrBatchSize)
            .Include(mr => mr.Repository)
            .Include(mr => mr.Task)
            .ToListAsync(ct);

    private async Task ArchiveMergeRequestBatchAsync(List<MergeRequest> mrs, CancellationToken ct)
    {
        var archived = mrs.Select(mr => new ArchivedMergeRequest
        {
            Id = mr.Id,
            ExternalId = mr.ExternalId,
            RepositoryName = mr.Repository.Name,
            SourceBranch = mr.SourceBranch,
            TargetBranch = mr.TargetBranch,
            Status = mr.Status.ToString(),
            TaskExternalId = mr.Task?.ExternalId,
            MergedAt = mr.MergedAt,
            ClosedAt = mr.ClosedAt,
            ArchivedAt = clock.GetUtcNow().UtcDateTime
        }).ToList();

        var ids = mrs.Select(mr => mr.Id).ToList();
        var existing = await archiveDb.ArchivedMergeRequests.AsNoTracking()
            .Where(a => ids.Contains(a.Id)).Select(a => a.Id).ToListAsync(ct);

        await InsertMissingAsync(archiveDb.ArchivedMergeRequests, archived, a => a.Id, [.. existing], ct);

        await db.MergeRequests.Where(mr => ids.Contains(mr.Id)).ExecuteDeleteAsync(ct);
    }

    // ---- tasks ---------------------------------------------------------------------------

    private Task<List<TaskItem>> LoadTaskBatchAsync(DateTime cutoff, CancellationToken ct) =>
        db.Tasks
            .AsNoTracking()
            .Where(t => t.ClosedAt < cutoff
                // Wait until the task's merge requests are archived. MergeRequest.TaskId is
                // SetNull, so deleting the task first would blank the link and the MR would
                // reach the archive without its TaskExternalId. This also subsumes the old
                // active-plan check: a plan only reaches a task through a merge request.
                && !db.MergeRequests.Any(mr => mr.TaskId == t.Id)
                // Wait until nothing depends on the task either. Archiving it earlier would
                // delete the edge out of a task that is still being planned, silently dropping
                // an ordering constraint, and would leave the dependent's DependenciesJson
                // incomplete when its own turn came. Batches re-query, so the graph drains
                // from its dependents downwards within the same cycle.
                && !db.TaskDependencies.Any(d => d.DependsOnTaskId == t.Id))
            .OrderBy(t => t.Id)
            .Take(options.TaskBatchSize)
            .Include(t => t.Dependencies)
            .ToListAsync(ct);

    private async Task ArchiveTaskBatchAsync(List<TaskItem> tasks, CancellationToken ct)
    {
        var archived = tasks.Select(t => new ArchivedTask
        {
            Id = t.Id,
            ExternalId = t.ExternalId,
            Title = t.Title,
            Status = t.Status,
            ClosedAt = t.ClosedAt,
            DependenciesJson = JsonSerializer.Serialize(t.Dependencies.Select(d => d.DependsOnTaskId)),
            ArchivedAt = clock.GetUtcNow().UtcDateTime
        }).ToList();

        var ids = tasks.Select(t => t.Id).ToList();
        var existing = await archiveDb.ArchivedTasks.AsNoTracking()
            .Where(a => ids.Contains(a.Id)).Select(a => a.Id).ToListAsync(ct);

        await InsertMissingAsync(archiveDb.ArchivedTasks, archived, a => a.Id, [.. existing], ct);

        var strategy = db.Database.CreateExecutionStrategy();
        await strategy.ExecuteAsync(async () =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);

            // TaskDependency points into Tasks twice, both Restrict. SQL Server rejects Cascade
            // on both (multiple cascade paths to one table), so the edges have to be removed
            // explicitly — otherwise any task holding a dependency fails to delete.
            await db.TaskDependencies
                .Where(d => ids.Contains(d.DependentTaskId) || ids.Contains(d.DependsOnTaskId))
                .ExecuteDeleteAsync(ct);
            await db.Tasks.Where(t => ids.Contains(t.Id)).ExecuteDeleteAsync(ct);

            await tx.CommitAsync(ct);
        });
    }

    // ---- batching ------------------------------------------------------------------------

    /// <summary>
    /// The archive and the operational database are separate SQL Server databases, so the
    /// insert and the delete cannot share a transaction. When the delete fails the rows are
    /// already archived and the next cycle selects them again — re-inserting the same primary
    /// keys would throw and wedge archiving for good. Skipping what is already stored also
    /// keeps the first copy, which still holds the dependency edges a later pass could no
    /// longer read.
    /// </summary>
    private async Task InsertMissingAsync<T>(
        DbSet<T> set,
        List<T> rows,
        Func<T, Guid> idOf,
        HashSet<Guid> alreadyArchived,
        CancellationToken ct) where T : class
    {
        var missing = rows.Where(r => !alreadyArchived.Contains(idOf(r))).ToList();
        if (missing.Count == 0) return;

        set.AddRange(missing);
        await archiveDb.SaveChangesAsync(ct);
    }

    private async Task RunBatchLoopAsync<T>(
        string entity,
        Func<CancellationToken, Task<List<T>>> loadBatch,
        Func<List<T>, CancellationToken, Task> archiveBatch,
        CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var count = await RunBatchWithRetriesAsync(entity, loadBatch, archiveBatch, ct);
            if (count is null or 0) return;

            logger.LogInformation("Archived {Count} {Entity}", count, entity);
            await Task.Delay(BatchPause, ct);
        }
    }

    /// <returns>Rows archived, 0 when nothing is left, or null when the batch was skipped.</returns>
    private async Task<int?> RunBatchWithRetriesAsync<T>(
        string entity,
        Func<CancellationToken, Task<List<T>>> loadBatch,
        Func<List<T>, CancellationToken, Task> archiveBatch,
        CancellationToken ct)
    {
        for (var attempt = 1; attempt <= MaxBatchAttempts; attempt++)
        {
            try
            {
                // A failed attempt leaves its Added entries tracked; without this the retry
                // would re-add them and insert every row twice.
                db.ChangeTracker.Clear();
                archiveDb.ChangeTracker.Clear();

                var batch = await loadBatch(ct);
                if (batch.Count == 0) return 0;

                await archiveBatch(batch, ct);
                return batch.Count;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (attempt < MaxBatchAttempts)
            {
                logger.LogWarning(ex, "Archiving a batch of {Entity} failed (attempt {Attempt}/{Max}), retrying",
                    entity, attempt, MaxBatchAttempts);
                await Task.Delay(RetryBackoff * attempt, ct);
            }
            catch (Exception ex)
            {
                // Re-querying would hand back the same failing rows, so skipping the batch
                // means skipping this entity until the next cycle.
                logger.LogError(ex, "Archiving a batch of {Entity} failed after {Max} attempts, skipping {Entity} for this cycle",
                    entity, MaxBatchAttempts, entity);
                return null;
            }
        }

        return null;
    }
}
