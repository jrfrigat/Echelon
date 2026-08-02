using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ReleaseOrchestrator.Application.DTOs;
using ReleaseOrchestrator.Application.Exceptions;
using ReleaseOrchestrator.Application.ReleasePlanning;
using ReleaseOrchestrator.Application.Services;
using ReleaseOrchestrator.Core.Enums;
using ReleaseOrchestrator.Infrastructure.Persistence;
using ReleaseOrchestrator.Infrastructure.Persistence.Models;

namespace ReleaseOrchestrator.Infrastructure.ReleasePlanning;

/// <summary>
/// Builds and reads per-task rollout plans by projecting the atlas onto one target task's
/// dependency closure (docs/issues/006-per-task-planning.md).
/// </summary>
/// <remarks>
/// The plan of record is persisted as <see cref="RolloutPlan"/> + its task nodes and items; the
/// execution waves shown to an operator are derived from the same pure graph the global planner
/// uses, so the two cannot disagree about ordering. Override replay (edits surviving a recalculate)
/// is a follow-up within P2; a generated plan currently reflects the atlas as-is.
/// </remarks>
public class RolloutPlanner(AppDbContext db, TimeProvider clock, ILogger<RolloutPlanner> logger) : IRolloutPlannerService
{
    /// <inheritdoc/>
    public async Task<int> CountTasksAsync(CancellationToken ct = default) =>
        await db.Tasks.CountAsync(ct);

    /// <inheritdoc/>
    public async Task<IReadOnlyList<TaskListItemDto>> ListTasksAsync(int page, int pageSize, CancellationToken ct = default)
    {
        var skip = (page - 1) * pageSize;
        return await db.Tasks
            .OrderBy(t => t.ExternalId).ThenBy(t => t.Id)
            .Skip(skip).Take(pageSize)
            .Select(t => new TaskListItemDto(
                t.Id,
                t.ExternalId,
                t.Title,
                t.Status,
                t.MergeRequests.Count,
                db.RolloutPlans.Any(p => p.TargetTaskId == t.Id && p.IsActive)))
            .AsNoTracking()
            .ToListAsync(ct);
    }

    /// <inheritdoc/>
    public async Task<TaskDetailDto?> GetTaskAsync(Guid taskId, CancellationToken ct = default) =>
        await db.Tasks
            .Where(t => t.Id == taskId)
            .Select(t => new TaskDetailDto(
                t.Id,
                t.ExternalId,
                t.Title,
                t.Status,
                t.ParentTask == null
                    ? null
                    : new TaskRefDto(t.ParentTask.Id, t.ParentTask.ExternalId, t.ParentTask.Title),
                // Ordered by key so the list is stable between reads; a task's subtasks have no
                // inherent order of their own, and they all deploy before it regardless.
                t.Children
                    .OrderBy(c => c.ExternalId)
                    .Select(c => new TaskRefDto(c.Id, c.ExternalId, c.Title))
                    .ToList()))
            .AsNoTracking()
            .FirstOrDefaultAsync(ct);

    /// <inheritdoc/>
    public async Task<RolloutPlanDto?> GetActivePlanAsync(Guid taskId, CancellationToken ct = default)
    {
        var plan = await db.RolloutPlans
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.TargetTaskId == taskId && p.IsActive, ct);
        if (plan is null) return null;

        var computed = await ComputeAsync(taskId, ct);
        return await BuildDtoAsync(plan, computed, ct);
    }

    /// <inheritdoc/>
    public async Task<RolloutPlanDto> RecalculateAsync(Guid taskId, ActorRef? actor, CancellationToken ct = default)
    {
        // Stamped before the read: anything committed before this instant is in the plan.
        var snapshotStartedAt = clock.GetUtcNow().UtcDateTime;

        var target = await db.Tasks.FirstOrDefaultAsync(t => t.Id == taskId, ct)
            ?? throw new NotFoundException($"Task {taskId} not found");

        var computed = await ComputeAsync(taskId, ct);
        var now = clock.GetUtcNow().UtcDateTime;

        var plan = new RolloutPlan
        {
            Id = Guid.NewGuid(),
            TargetTaskId = taskId,
            Version = now.ToString("yyyyMMddHHmmss"),
            Source = PlanSource.Generated,
            Status = PlanStatus.Ready,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
            SnapshotStartedAt = snapshotStartedAt,
            ConflictsJson = computed.Graph.Conflicts.Count == 0 ? null : JsonSerializer.Serialize(computed.Graph.Conflicts),
            ContentHash = ComputeContentHash(computed),
            // Null for the recalculation consumer, which rebuilds every active plan on every
            // ingestion event. Stamped inside the same transaction as the plan below, so authorship
            // cannot commit without the version it describes.
            CreatedByOid = actor?.Oid,
            CreatedByKind = actor?.Kind,
            CreatedByName = actor?.DisplayName,
            Nodes = []
        };

        // A node per closure task -- including tasks with no merge requests, so the tree shows the
        // whole closure. Items hang off their task's node.
        foreach (var closureTaskId in computed.Closure)
        {
            var node = new PlanTaskNode { Id = Guid.NewGuid(), RolloutPlanId = plan.Id, TaskId = closureTaskId, Items = [] };
            foreach (var mr in computed.PlanMrs.Where(m => m.TaskId == closureTaskId))
                node.Items.Add(new PlanItem { Id = Guid.NewGuid(), PlanTaskNodeId = node.Id, MergeRequestId = mr.Id });
            plan.Nodes.Add(node);
        }

        // One active plan per task: deactivate the previous one and insert in a single transaction,
        // so a crash between the two never leaves the task with no active plan and two concurrent
        // recalculations cannot both leave one active (the filtered unique index is the backstop).
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.RolloutPlans
            .Where(p => p.TargetTaskId == taskId && p.IsActive)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.IsActive, false), ct);
        db.RolloutPlans.Add(plan);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        if (computed.Graph.Conflicts.Count > 0)
            logger.LogWarning(
                "Rollout plan for task {Task} built with {Count} dropped dependency link(s); the order violates them.",
                target.ExternalId, computed.Graph.Conflicts.Count);

        return await BuildDtoAsync(plan, computed, ct);
    }

    /// <summary>
    /// Fingerprints what the plan says, so a rebuild that changed nothing can be told from one that did.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Hashes the ORDERED STAGES — the actual deploy order — plus the closure and the dropped
    /// constraints. Hashing which merge requests are in the plan would be cheaper and wrong: adding
    /// a repository-ordering rule reorders the stages while membership is untouched, so a
    /// membership fingerprint would report "plan unchanged" about the one edit whose entire purpose
    /// was to change the order.
    /// </para>
    /// <para>
    /// The closure is included because a prerequisite task joining or leaving changes what the
    /// rollout covers even when no merge request moves; the conflicts because a plan that started
    /// violating a constraint is a different plan, whatever its order.
    /// </para>
    /// </remarks>
    private static string ComputeContentHash(Computed computed)
    {
        var builder = new StringBuilder();

        // Stage index included explicitly: without it, moving a merge request between two adjacent
        // stages would produce the same flattened sequence of ids.
        for (int stage = 0; stage < computed.Graph.Stages.Count; stage++)
        {
            builder.Append(stage).Append(':');
            foreach (var mrId in computed.Graph.Stages[stage])
                builder.Append(mrId.ToString("N")).Append(',');
            builder.Append(';');
        }

        builder.Append('|');
        // Sorted: the closure is a set, and its enumeration order must not make an unchanged plan
        // look changed.
        foreach (var closureTaskId in computed.Closure.OrderBy(id => id))
            builder.Append(closureTaskId.ToString("N")).Append(',');

        builder.Append('|');
        foreach (var conflict in computed.Graph.Conflicts.OrderBy(c => c.FromMrId).ThenBy(c => c.ToMrId))
            builder.Append(conflict.DroppedEdgeKind).Append(':')
                   .Append(conflict.FromMrId.ToString("N")).Append("->")
                   .Append(conflict.ToMrId.ToString("N")).Append(',');

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
    }

    private sealed record Computed(
        HashSet<Guid> Closure,
        IReadOnlyDictionary<Guid, IReadOnlyList<Guid>> Adjacency,
        List<PlanMergeRequest> PlanMrs,
        PlanGraphResult Graph);

    /// <summary>Computes the target's closure and its ordered plan from the atlas -- shared by build and read.</summary>
    private async Task<Computed> ComputeAsync(Guid taskId, CancellationToken ct)
    {
        var adjacency = await LoadAdjacencyAsync(ct);
        var closure = PlanClosureBuilder.Closure(taskId, adjacency).ToHashSet();
        var closureIds = closure.Select(id => (Guid?)id).ToList();

        var planMrs = await db.MergeRequests
            // A closed (abandoned) merge request is not part of the rollout; everything else the
            // task owns is, so the tree shows the full picture. The executor decides at launch what
            // is already deployed and skips it (docs/issues/007-execution-engine.md).
            .Where(mr => closureIds.Contains(mr.TaskId) && mr.Status != MergeRequestStatus.Closed)
            .OrderBy(mr => mr.CreatedAt).ThenBy(mr => mr.Id)
            .Select(PlanInput.FromEntity)
            .AsSplitQuery()
            .AsNoTracking()
            .ToListAsync(ct);

        // Replayed here, on every recalculation: this is what makes a hand-ordered plan survive the
        // next ingestion event instead of being silently rebuilt without the operator's edits.
        var (add, remove) = await LoadOverridesAsync(taskId, ct);

        return new Computed(closure, adjacency, planMrs, ReleasePlanGraph.Build(planMrs, add, remove));
    }

    /// <summary>Loads the whole task graph as adjacency, with the wait policy applied.</summary>
    /// <remarks>
    /// Two sources, one relation. Declared dependencies name who waits directly; the hierarchy says a
    /// parent waits on its children. Whether each counts is now a decision rather than an assumption
    /// — see <see cref="TaskWaitPolicy"/> — and the merge itself moved to the pure
    /// <see cref="TaskWaitGraph"/>, so it is testable without a database. What is left here is the
    /// loading.
    ///
    /// This adjacency decides the closure — which tasks a rollout even includes — so a source omitted
    /// here is a plan that silently leaves those tasks out, no matter what <see cref="PlanInput"/>
    /// says about the edges between their merge requests. That is the same reason the policy has to
    /// be applied at this level and not later.
    /// </remarks>
    private async Task<Dictionary<Guid, IReadOnlyList<Guid>>> LoadAdjacencyAsync(CancellationToken ct)
    {
        var linked = await db.TaskDependencies
            .Select(d => new { d.DependentTaskId, d.DependsOnTaskId })
            .AsNoTracking()
            .ToListAsync(ct);

        var hierarchy = await db.Tasks
            .Where(t => t.ParentTaskId != null)
            .Select(t => new { DependentTaskId = t.ParentTaskId!.Value, DependsOnTaskId = t.Id })
            .AsNoTracking()
            .ToListAsync(ct);

        var manualOrder = await db.TaskPrerequisiteOrders
            .OrderBy(o => o.TaskId).ThenBy(o => o.Position)
            .Select(o => new { o.TaskId, o.PrerequisiteTaskId })
            .AsNoTracking()
            .ToListAsync(ct);

        var overrides = await db.Tasks
            .Where(t => t.WaitForSubtasks != null || t.WaitForLinked != null || t.PrerequisiteGroupOrder != null)
            .Select(t => new { t.Id, t.WaitForSubtasks, t.WaitForLinked, t.PrerequisiteGroupOrder })
            .AsNoTracking()
            .ToListAsync(ct);

        var global = await LoadGlobalPolicyAsync(ct);
        var overrideById = overrides.ToDictionary(o => o.Id);

        TaskWaitPolicy PolicyFor(Guid taskId) =>
            overrideById.TryGetValue(taskId, out var o)
                ? global.OverriddenBy(o.WaitForSubtasks, o.WaitForLinked, o.PrerequisiteGroupOrder)
                : global;

        var subtasksOf = hierarchy.ToLookup(h => h.DependentTaskId, h => h.DependsOnTaskId);
        var linkedOf = linked.ToLookup(l => l.DependentTaskId, l => l.DependsOnTaskId);
        var orderOf = manualOrder.ToLookup(o => o.TaskId, o => o.PrerequisiteTaskId);

        var tasks = subtasksOf.Select(g => g.Key)
            .Concat(linkedOf.Select(g => g.Key))
            .Distinct()
            .Select(id => new TaskPrerequisites(
                id, [.. subtasksOf[id]], [.. linkedOf[id]], [.. orderOf[id]]));

        return TaskWaitGraph.Build(tasks, PolicyFor);
    }

    /// <summary>
    /// The installation-wide wait policy, or the built-in default when nobody has saved one.
    /// </summary>
    /// <remarks>
    /// Absent settings mean the defaults, not a failure: a fresh installation has no row, and it must
    /// plan exactly as it did before the policy existed rather than refuse to plan at all.
    /// </remarks>
    private async Task<TaskWaitPolicy> LoadGlobalPolicyAsync(CancellationToken ct)
    {
        var settings = await db.PlanningSettings.AsNoTracking().FirstOrDefaultAsync(ct);

        return settings is null
            ? TaskWaitPolicy.Default
            : new TaskWaitPolicy(settings.WaitForSubtasks, settings.WaitForLinked, settings.PrerequisiteGroupOrder);
    }

    /// <summary>
    /// The operator's ordering edits for a task, as the pure graph takes them.
    /// </summary>
    /// <remarks>
    /// Replayed on every recalculation, which is the point of storing them as deltas against the task
    /// rather than baking them into a plan the next ingestion event would replace. An edit naming a
    /// merge request no longer in the plan is simply ignored by <c>ReleasePlanGraph</c>, which drops
    /// any edge whose endpoints it was not given.
    /// </remarks>
    private async Task<(List<(Guid From, Guid To)> Add, List<(Guid From, Guid To)> Remove)> LoadOverridesAsync(
        Guid taskId, CancellationToken ct)
    {
        var rows = await db.PlanOverrides
            .Where(o => o.TaskId == taskId
                        && (o.Kind == PlanOverrideKind.AddEdge || o.Kind == PlanOverrideKind.RemoveEdge))
            .Select(o => new { o.Kind, o.Payload })
            .AsNoTracking()
            .ToListAsync(ct);

        List<(Guid, Guid)> add = [];
        List<(Guid, Guid)> remove = [];

        foreach (var row in rows)
        {
            var edge = DeserializeEdge(row.Payload);
            if (edge is null) continue;

            if (row.Kind == PlanOverrideKind.AddEdge) add.Add(edge.Value);
            else remove.Add(edge.Value);
        }

        return (add, remove);
    }

    /// <summary>
    /// Reads an edge delta's payload, answering null for one that cannot be read.
    /// </summary>
    /// <remarks>
    /// Null rather than throwing: a payload written by an older shape must not make the task
    /// unplannable. The edit is dropped and the plan is the derived one, which is the safe direction
    /// — the alternative is an operator whose plan screen 500s with no way to clear the bad row.
    /// </remarks>
    private static (Guid From, Guid To)? DeserializeEdge(string payload)
    {
        try
        {
            var edge = JsonSerializer.Deserialize<PlanEdgeOverridePayload>(payload);
            return edge is null || edge.From == Guid.Empty || edge.To == Guid.Empty
                ? null
                : (edge.From, edge.To);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The stored shape of an <c>AddEdge</c>/<c>RemoveEdge</c> delta.</summary>
    /// <param name="From">The merge request that deploys first.</param>
    /// <param name="To">The merge request that waits.</param>
    private sealed record PlanEdgeOverridePayload(Guid From, Guid To);

    private async Task<RolloutPlanDto> BuildDtoAsync(RolloutPlan plan, Computed c, CancellationToken ct)
    {
        var taskById = (await db.Tasks
                .Where(t => c.Closure.Contains(t.Id))
                .Select(t => new { t.Id, t.ExternalId, t.Title })
                .AsNoTracking()
                .ToListAsync(ct))
            .ToDictionary(t => t.Id);

        var mrIds = c.PlanMrs.Select(m => m.Id).ToList();
        var mrById = (await db.MergeRequests
                .Where(m => mrIds.Contains(m.Id))
                .Select(m => new
                {
                    m.Id, m.ExternalId, m.SourceBranch, m.TargetBranch,
                    RepoName = m.Repository.Name, m.Status
                })
                .AsNoTracking()
                .ToListAsync(ct))
            .ToDictionary(m => m.Id);

        var waveOf = new Dictionary<Guid, int>();
        for (int i = 0; i < c.Graph.Stages.Count; i++)
            foreach (var id in c.Graph.Stages[i]) waveOf[id] = i + 1;

        var nodes = c.Closure
            .Where(taskById.ContainsKey)
            .Select(tid =>
            {
                var deps = c.Adjacency.TryGetValue(tid, out var d)
                    ? d.Where(c.Closure.Contains).ToList()
                    : [];
                var items = c.PlanMrs
                    .Where(m => m.TaskId == tid)
                    .Select(m => mrById[m.Id])
                    .OrderBy(m => m.RepoName).ThenBy(m => m.ExternalId)
                    .Select(m => new PlanItemDto(
                        m.Id, m.ExternalId, m.RepoName, m.SourceBranch, m.TargetBranch,
                        m.Status.ToString(), waveOf.GetValueOrDefault(m.Id)))
                    .ToList();
                var info = taskById[tid];
                return new PlanTaskNodeDto(tid, info.ExternalId, info.Title, tid == plan.TargetTaskId, deps, items);
            })
            .OrderByDescending(n => n.IsTarget).ThenBy(n => n.TaskKey)
            .ToList();

        var waves = c.Graph.Stages.Select((stage, i) => new PlanWaveDto(i + 1, stage)).ToList();
        var conflicts = c.Graph.Conflicts
            .Select(x => new PlanConflictDto(x.DroppedEdgeKind.ToString(), x.FromMrId, x.ToMrId, x.Reason))
            .ToList();

        var targetKey = taskById.TryGetValue(plan.TargetTaskId, out var tt) ? tt.ExternalId : string.Empty;

        return new RolloutPlanDto(
            plan.Id, plan.TargetTaskId, targetKey,
            plan.Version, plan.Source.ToString(), plan.Status.ToString(), plan.IsActive,
            plan.CreatedAt, plan.UpdatedAt, nodes, waves, conflicts);
    }
}
