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

    public Task ArchiveMergeRequestsAsync(DateTime cutoff, CancellationToken ct) =>
        RunBatchLoopAsync("merge requests", c => LoadMergeRequestBatchAsync(cutoff, c), ArchiveMergeRequestBatchAsync, ct);

    public Task ArchiveTasksAsync(DateTime cutoff, CancellationToken ct) =>
        RunBatchLoopAsync("tasks", c => LoadTaskBatchAsync(cutoff, c), ArchiveTaskBatchAsync, ct);

    /// <summary>
    /// Deletes merge-request status transitions past their retention window.
    /// </summary>
    /// <param name="now">The current time; the cutoff is measured back from here.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <remarks>
    /// Delete-only, unlike every other phase here, and deliberately so: these rows are read by the
    /// task timeline and by nothing else, so moving them to a third database would preserve data no
    /// tool can display. The journal has no foreign key in either direction, which is what makes
    /// this safe to run independently of the archive phases above.
    /// </remarks>
    public async Task PruneStatusJournalAsync(DateTime now, CancellationToken ct)
    {
        var cutoff = now.AddDays(-options.StatusJournalRetentionDays);

        var removed = await db.MergeRequestStatusChanges
            .Where(c => c.At < cutoff)
            .ExecuteDeleteAsync(ct);

        if (removed > 0)
            logger.LogInformation("Pruned {Count} merge-request status transition(s) older than {Cutoff:u}", removed, cutoff);
    }

    /// <summary>
    /// Prunes label-change journal rows past the retention window.
    /// </summary>
    /// <param name="now">The current time, UTC.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <remarks>
    /// The sibling of <see cref="PruneStatusJournalAsync"/>, and under the same retention: a merge
    /// request's status history and label history are read together in the timeline, so they age out
    /// together. Like the status journal this table is foreign-key-free, which is what makes pruning
    /// it safe to run on its own schedule independent of the archive phases. Without this the table
    /// grew without bound -- every label change on every merge request, kept forever.
    /// </remarks>
    public async Task PruneLabelJournalAsync(DateTime now, CancellationToken ct)
    {
        var cutoff = now.AddDays(-options.StatusJournalRetentionDays);

        var removed = await db.MergeRequestLabelChanges
            .Where(c => c.At < cutoff)
            .ExecuteDeleteAsync(ct);

        if (removed > 0)
            logger.LogInformation("Pruned {Count} merge-request label change(s) older than {Cutoff:u}", removed, cutoff);
    }

    // ---- merge requests ------------------------------------------------------------------

    private Task<List<MergeRequest>> LoadMergeRequestBatchAsync(DateTime cutoff, CancellationToken ct) =>
        db.MergeRequests
            .AsNoTracking()
            .Where(mr => ((mr.Status == MergeRequestStatus.Merged && mr.MergedAt < cutoff)
                    || (mr.Status == MergeRequestStatus.Closed && mr.ClosedAt < cutoff))
                // The per-task plan, the rollout history and the deployment state each reference a
                // merge request with Restrict, so it cannot be deleted while any of them survive. A
                // merge request that never entered a rollout (closed without deploying) has none and
                // is archived here; one that did stays until its rollout history is cleared -- a
                // follow-up, as nothing prunes that yet. Without this gate the delete FK-violates and
                // the batch wedges, exactly as the old StageItem gate prevented for the global plan.
                && !db.PlanItems.Any(pi => pi.MergeRequestId == mr.Id)
                && !db.RolloutSteps.Any(rs => rs.MergeRequestId == mr.Id)
                && !db.MrDeploymentStates.Any(s => s.MergeRequestId == mr.Id)
                && !db.MrDeployClaims.Any(c => c.MergeRequestId == mr.Id))
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
            Labels = mr.Labels,
            TaskExternalId = mr.Task?.ExternalId,
            // The source row is deleted below, and this column exists nowhere else -- not copying it
            // does not hide the merge request's opening from a task's history, it destroys it.
            CreatedAt = mr.CreatedAt,
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
                && !db.TaskDependencies.Any(d => d.DependsOnTaskId == t.Id)
                // Same reasoning for the hierarchy: a parent waits on its children, so archiving a
                // task while a child still points at it (ParentTaskId, Restrict and self-referencing)
                // would both FK-violate the delete and drop the parent's ordering edge. Children
                // archive first; the parent drains once none reference it.
                && !db.Tasks.Any(c => c.ParentTaskId == t.Id)
                // The per-task plan and the rollout history reference the task with Restrict
                // (PlanTaskNode.TaskId, RolloutPlan/Rollout.TargetTaskId, RolloutStep.TaskId), and a
                // prerequisite task can hold a PlanTaskNode with no merge requests of its own -- so
                // the two gates above do not cover it. Without these the ExecuteDeleteAsync below
                // FK-violates and wedges the whole task batch. Nothing prunes rollout history yet, so
                // such a task simply waits (a follow-up), rather than failing every nightly cycle.
                && !db.PlanTaskNodes.Any(n => n.TaskId == t.Id)
                && !db.RolloutPlans.Any(p => p.TargetTaskId == t.Id)
                && !db.Rollouts.Any(r => r.TargetTaskId == t.Id)
                && !db.RolloutSteps.Any(s => s.TaskId == t.Id))
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
            FirstSeenAt = t.FirstSeenAt,
            FirstSeenSource = t.FirstSeenSource,
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
