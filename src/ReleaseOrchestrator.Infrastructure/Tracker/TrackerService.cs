using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ReleaseOrchestrator.Application.Services;
using ReleaseOrchestrator.Infrastructure.Persistence.Models;
using ReleaseOrchestrator.Infrastructure.Providers;
using ReleaseOrchestrator.Infrastructure.Persistence;
using ReleaseOrchestrator.Providers.Abstractions.Tracker;

namespace ReleaseOrchestrator.Infrastructure.Tracker;

/// <summary>
/// Reads tasks and their dependency links from a tracker into the local model.
///
/// This is the only source of TaskDependency rows, and therefore of every task edge in the
/// release plan. It used to be unreachable - registered in DI and injected nowhere - so the
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

        var provider = await providerFactory.CreateAsync(conn.ToDescriptor(), ct);

        var info = await provider.GetIssueAsync(externalTaskId, ct);
        if (info is null)
        {
            logger.LogInformation("Task {Task} does not exist in tracker {Tracker}", externalTaskId, conn.Name);
            return false;
        }

        var task = await UpsertTaskAsync(conn.Id, provider, info, $"tracker/{conn.Name}", ct);
        var parentChanged = await ApplyParentAsync(conn, provider, task, info.ParentKey, ct);
        var links = await ReadDependenciesAsync(provider, conn, externalTaskId, ct);

        var changed = await ReplaceDependenciesAsync(conn, provider, task, links, ct);

        await db.SaveChangesAsync(ct);
        return changed || parentChanged;
    }

    /// <summary>
    /// Points the task at its parent, or clears the link when the tracker no longer reports one.
    /// </summary>
    /// <returns>True when the parent actually changed - the caller only replans then.</returns>
    /// <remarks>
    /// Deliberately here and not in <see cref="UpsertTaskAsync"/>. Resolving a parent may fetch it,
    /// and fetching goes through the same upsert: setting the parent there would make every fetch
    /// resolve its own parent in turn, walking the hierarchy to its root on every sync and never
    /// terminating at all if a tracker reports a cycle. The fetch stays shallow, exactly as it does
    /// for prerequisites - a fetched parent gets its own parent when it is itself synced.
    ///
    /// A hierarchy cycle a tracker does report is not fatal downstream: the closure builder expands
    /// each task once and the planner breaks cycles and records the conflict, so it degrades to a
    /// reported conflict rather than a hang.
    /// </remarks>
    private async Task<bool> ApplyParentAsync(
        TrackerConnection conn,
        ITrackerProvider provider,
        TaskItem task,
        string? parentKey,
        CancellationToken ct)
    {
        Guid? parentId = null;

        if (!string.IsNullOrWhiteSpace(parentKey))
        {
            parentId = await ResolveOrFetchTaskAsync(conn, provider, parentKey, ct);

            // A task that is its own parent carries no ordering and would be a self-edge in the plan.
            if (parentId == task.Id) parentId = null;
        }

        if (task.ParentTaskId == parentId) return false;

        logger.LogInformation(
            "Task {Task}: parent {Parent}", task.ExternalId, parentKey is { Length: > 0 } ? parentKey : "cleared");

        task.ParentTaskId = parentId;
        return true;
    }

    private async Task<IReadOnlyList<TrackerIssueDependency>> ReadDependenciesAsync(
        ITrackerProvider provider,
        TrackerConnection conn,
        string externalTaskId,
        CancellationToken ct)
    {
        // The "can this provider do it at all" question, answered by the type system rather than
        // by making the call and reading the failure. A tracker with no link model returns no
        // edges, which is indistinguishable from an issue that has none - so the difference has
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

    /// <param name="origin">
    /// How this task came to be stored, recorded on insert. Required rather than defaulted: the two
    /// callers mean genuinely different things - one is a task being synced in its own right, the
    /// other is a task pulled in only because something else referenced it - and a default would let
    /// the second silently inherit the first's story.
    /// </param>
    private async Task<TaskItem> UpsertTaskAsync(
        Guid connectionId, ITrackerProvider provider, TrackerIssue info, string? origin, CancellationToken ct)
    {
        var task = await db.Tasks.FirstOrDefaultAsync(
            t => t.TrackerConnectionId == connectionId && t.ExternalId == info.Key, ct);

        if (task is null)
        {
            task = new TaskItem
            {
                Id = Guid.NewGuid(),
                ExternalId = info.Key,
                TrackerConnectionId = connectionId,
                FirstSeenAt = clock.GetUtcNow().UtcDateTime,
                FirstSeenSource = origin
            };
            db.Tasks.Add(task);
        }

        task.Title = info.Summary;
        task.Status = info.StatusKey;
        // Derived from the status rather than trusted from ResolvedAt: a tracker can report a
        // resolution time for a status we do not treat as closed, and archiving keys off this.
        // The provider owns which statuses are closed - the set is its vocabulary, not ours.
        task.ClosedAt = provider.IsClosedStatus(info.StatusKey)
            ? info.ResolvedAt ?? clock.GetUtcNow().UtcDateTime
            : null;

        // Flush so the row has an identity before edges reference it.
        await db.SaveChangesAsync(ct);
        return task;
    }

    /// <returns>True when the set of edges actually changed - the caller only replans then.</returns>
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
        // all - silently dropping ordering constraints that do exist.
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
    /// Finds a referenced task - a prerequisite or a parent - fetching it from the tracker if it is
    /// not stored yet.
    ///
    /// Skipping an unknown reference - as this used to - loses the edge permanently: nothing
    /// revisits the referring task once the other appears, so the plan silently omits a
    /// constraint that the tracker states. Sync order is not something we control, so a task
    /// referencing one we have not imported yet is ordinary, not exceptional.
    ///
    /// The fetch is deliberately shallow - the fetched task's own links and parent are left to its
    /// own sync, which bounds this at one extra call per edge instead of walking the graph.
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
            // Said of the referenced key alone: this resolves both prerequisites and parents, and the
            // message used to name the same key as both ends ("X depends on X"), which read as a
            // self-dependency that was never there.
            logger.LogWarning(
                "Task {Missing} is referenced but tracker {Tracker} does not return it; the link is skipped.",
                key, conn.Name);
            return null;
        }

        // Not an arrival: this task was fetched only because another one named it as a prerequisite
        // or a parent. Recording the tracker as its source would assert an announcement that never
        // happened, which is exactly the kind of confident-but-wrong entry the timeline must not
        // contain.
        var fetched = await UpsertTaskAsync(conn.Id, provider, info, "dependency-closure", ct);
        return fetched.Id;
    }
}
