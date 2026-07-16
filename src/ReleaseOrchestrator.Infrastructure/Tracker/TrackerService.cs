using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ReleaseOrchestrator.Application.Services;
using ReleaseOrchestrator.Core.Entities;
using ReleaseOrchestrator.Infrastructure.Persistence;
using ReleaseOrchestrator.Providers.Abstractions.Tracker;

namespace ReleaseOrchestrator.Infrastructure.Tracker;

/// <summary>
/// Reads tasks and their dependency links from a tracker into the local model.
///
/// This is the only source of TaskDependency rows, and therefore of every task edge in the
/// release plan. It used to be unreachable — registered in DI and injected nowhere — so the
/// table stayed empty and plans were ordered by stack links alone.
/// </summary>
/// <remarks>
/// Provider-agnostic: which tracker answers, and what it needs to be asked, is the factory's and
/// the adapter's business.
/// </remarks>
public class TrackerService(
    AppDbContext db,
    ITrackerProviderFactory providerFactory,
    TimeProvider clock,
    ILogger<TrackerService> logger) : ITrackerService
{
    /// <inheritdoc/>
    public async Task<bool> SyncTaskAsync(Guid trackerConnectionId, string externalTaskId, CancellationToken ct)
    {
        var conn = await db.TrackerConnections.FirstOrDefaultAsync(c => c.Id == trackerConnectionId, ct)
            ?? throw new InvalidOperationException($"TrackerConnection {trackerConnectionId} not found");

        var provider = await providerFactory.CreateAsync(conn, ct);

        var info = await provider.GetIssueAsync(externalTaskId, ct);
        if (info is null)
        {
            logger.LogInformation("Task {Task} does not exist in tracker {Tracker}", externalTaskId, conn.Name);
            return false;
        }

        var task = await UpsertTaskAsync(conn.Id, provider, info, ct);
        var links = await ReadDependenciesAsync(provider, conn, externalTaskId, ct);

        var changed = await ReplaceDependenciesAsync(conn, provider, task, links, ct);

        await db.SaveChangesAsync(ct);
        return changed;
    }

    private async Task<IReadOnlyList<TrackerIssueDependency>> ReadDependenciesAsync(
        ITrackerProvider provider,
        TrackerConnection conn,
        string externalTaskId,
        CancellationToken ct)
    {
        // The "can this provider do it at all" question, answered by the type system rather than
        // by making the call and reading the failure. A tracker with no link model returns no
        // edges, which is indistinguishable from an issue that has none — so the difference has
        // to be visible before the call, not after.
        if (provider is not ITrackerDependencySource source)
        {
            logger.LogDebug(
                "Tracker {Tracker} does not report dependency links; task {Task} contributes no edges.",
                conn.Name, externalTaskId);
            return [];
        }

        return await source.GetIssueDependenciesAsync(externalTaskId, ct);
    }

    private async Task<TaskItem> UpsertTaskAsync(Guid connectionId, ITrackerProvider provider, TrackerIssue info, CancellationToken ct)
    {
        var task = await db.Tasks.FirstOrDefaultAsync(
            t => t.TrackerConnectionId == connectionId && t.ExternalId == info.Key, ct);

        if (task is null)
        {
            task = new TaskItem
            {
                Id = Guid.NewGuid(),
                ExternalId = info.Key,
                TrackerConnectionId = connectionId
            };
            db.Tasks.Add(task);
        }

        task.Title = info.Summary;
        task.Status = info.StatusKey;
        // Derived from the status rather than trusted from ResolvedAt: a tracker can report a
        // resolution time for a status we do not treat as closed, and archiving keys off this.
        // The provider owns which statuses are closed — the set is its vocabulary, not ours.
        task.ClosedAt = provider.IsClosedStatus(info.StatusKey)
            ? info.ResolvedAt ?? clock.GetUtcNow().UtcDateTime
            : null;

        // Flush so the row has an identity before edges reference it.
        await db.SaveChangesAsync(ct);
        return task;
    }

    /// <returns>True when the set of edges actually changed — the caller only replans then.</returns>
    private async Task<bool> ReplaceDependenciesAsync(
        TrackerConnection conn,
        ITrackerProvider provider,
        TaskItem task,
        IReadOnlyList<TrackerIssueDependency> links,
        CancellationToken ct)
    {
        var wanted = new List<Guid>();

        foreach (var key in links.Select(l => l.DependsOnKey).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var dependsOnId = await ResolveOrFetchTaskAsync(conn, provider, key, ct);
            if (dependsOnId is null || dependsOnId == task.Id) continue;   // self-links carry no order
            wanted.Add(dependsOnId.Value);
        }

        var existing = await db.TaskDependencies
            .Where(d => d.DependentTaskId == task.Id)
            .ToListAsync(ct);

        var existingIds = existing.Select(d => d.DependsOnTaskId).ToHashSet();
        var wantedIds = wanted.ToHashSet();

        // Diff instead of delete-all-then-reinsert: rewriting every row on every sync churns the
        // table and, on a failure between the two steps, leaves the task with no dependencies at
        // all — silently dropping ordering constraints that do exist.
        var toRemove = existing.Where(d => !wantedIds.Contains(d.DependsOnTaskId)).ToList();
        var toAdd = wantedIds.Where(id => !existingIds.Contains(id)).ToList();

        if (toRemove.Count == 0 && toAdd.Count == 0) return false;

        db.TaskDependencies.RemoveRange(toRemove);
        foreach (var dependsOnId in toAdd)
            db.TaskDependencies.Add(new TaskDependency
            {
                Id = Guid.NewGuid(),
                DependentTaskId = task.Id,
                DependsOnTaskId = dependsOnId
            });

        logger.LogInformation(
            "Task {Task}: {Added} dependency link(s) added, {Removed} removed", task.ExternalId, toAdd.Count, toRemove.Count);

        return true;
    }

    /// <summary>
    /// Finds the prerequisite task, fetching it from the tracker if it is not stored yet.
    ///
    /// Skipping an unknown prerequisite — as this used to — loses the edge permanently: nothing
    /// revisits the dependent task once the prerequisite appears, so the plan silently omits a
    /// constraint that the tracker states. Sync order is not something we control, so a task
    /// referencing one we have not imported yet is ordinary, not exceptional.
    ///
    /// The fetch is deliberately shallow — the prerequisite's own links are left to its own sync,
    /// which bounds this at one extra call per edge instead of walking the graph.
    /// </summary>
    private async Task<Guid?> ResolveOrFetchTaskAsync(TrackerConnection conn, ITrackerProvider provider, string key, CancellationToken ct)
    {
        var existingId = await db.Tasks
            .Where(t => t.TrackerConnectionId == conn.Id && t.ExternalId == key)
            .Select(t => (Guid?)t.Id)
            .FirstOrDefaultAsync(ct);

        if (existingId is not null) return existingId;

        var info = await provider.GetIssueAsync(key, ct);
        if (info is null)
        {
            logger.LogWarning(
                "Task {Task} depends on {Missing}, which the tracker does not return; the link is skipped.",
                key, key);
            return null;
        }

        var fetched = await UpsertTaskAsync(conn.Id, provider, info, ct);
        return fetched.Id;
    }
}
