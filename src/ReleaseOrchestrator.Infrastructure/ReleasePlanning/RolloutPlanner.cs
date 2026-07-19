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
    public async Task<RolloutPlanDto> RecalculateAsync(Guid taskId, CancellationToken ct = default)
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

        return new Computed(closure, adjacency, planMrs, ReleasePlanGraph.Build(planMrs));
    }

    /// <summary>Loads the whole task-dependency graph as adjacency. A recursive walk is a later optimization.</summary>
    /// <remarks>
    /// Two sources, one relation. Declared dependencies name who waits directly; the hierarchy says a
    /// parent waits on its children, because a parent is the umbrella over the concrete work its
    /// subtasks carry. Both end up as "tasks this one deploys after", and both have to be here: this
    /// adjacency decides the closure — which tasks a rollout even includes — so a parent missing its
    /// children here would produce a plan that silently leaves them out, no matter what
    /// <see cref="PlanInput"/> says about the edges between their merge requests.
    /// </remarks>
    private async Task<Dictionary<Guid, IReadOnlyList<Guid>>> LoadAdjacencyAsync(CancellationToken ct)
    {
        var edges = await db.TaskDependencies
            .Select(d => new { d.DependentTaskId, d.DependsOnTaskId })
            .AsNoTracking()
            .ToListAsync(ct);

        var hierarchy = await db.Tasks
            .Where(t => t.ParentTaskId != null)
            .Select(t => new { DependentTaskId = t.ParentTaskId!.Value, DependsOnTaskId = t.Id })
            .AsNoTracking()
            .ToListAsync(ct);

        return edges.Concat(hierarchy)
            .GroupBy(e => e.DependentTaskId)
            .ToDictionary(
                g => g.Key,
                // Distinct: a tracker may state a dependency that the hierarchy already implies, and
                // the same prerequisite twice would be the same edge twice in the graph.
                g => (IReadOnlyList<Guid>)g.Select(e => e.DependsOnTaskId).Distinct().ToList());
    }

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
