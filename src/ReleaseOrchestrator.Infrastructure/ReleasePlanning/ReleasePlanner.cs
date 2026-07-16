using Microsoft.EntityFrameworkCore;
using ReleaseOrchestrator.Application.DTOs;
using ReleaseOrchestrator.Application.Services;
using ReleaseOrchestrator.Core.Entities;
using ReleaseOrchestrator.Core.Enums;
using ReleaseOrchestrator.Infrastructure.Persistence;

namespace ReleaseOrchestrator.Infrastructure.ReleasePlanning;

public class ReleasePlanner(AppDbContext db) : IReleasePlannerService
{
    public async Task<ReleasePlanDto> RecalculateAsync(CancellationToken ct = default)
    {
        var readyMrs = await db.MergeRequests
            .Include(mr => mr.Task)
                .ThenInclude(t => t!.Dependencies)
            .Include(mr => mr.Repository)
                .ThenInclude(r => r.RepositoryStacks)
                    .ThenInclude(rs => rs.Stack)
                        .ThenInclude(s => s.DependentOn)
            .Where(mr => mr.Status == MergeRequestStatus.ReadyForDeploy)
            .AsNoTracking()
            .ToListAsync(ct);

        var stages = BuildStages(readyMrs);
        var full = await PersistPlanAsync(stages, ct);
        return MapToDto(full);
    }

    public async Task<ReleasePlanDto?> GetActiveAsync(CancellationToken ct = default)
    {
        var plan = await LoadFullPlanAsync(p => p.IsActive, ct);
        return plan is null ? null : MapToDto(plan);
    }

    public async Task<ReleasePlanDto?> GetByIdAsync(Guid planId, CancellationToken ct = default)
    {
        var plan = await LoadFullPlanAsync(p => p.Id == planId, ct);
        return plan is null ? null : MapToDto(plan);
    }

    public async Task<ReleasePlanDto> ReorderStagesAsync(Guid planId, List<Guid> orderedStageIds, CancellationToken ct = default)
    {
        var plan = await db.ReleasePlans
            .Include(p => p.Stages)
            .FirstOrDefaultAsync(p => p.Id == planId, ct)
            ?? throw new InvalidOperationException($"Plan {planId} not found");

        var stageMap = plan.Stages.ToDictionary(s => s.Id);
        foreach (var stageId in orderedStageIds)
        {
            if (!stageMap.ContainsKey(stageId))
                throw new InvalidOperationException($"Stage {stageId} does not belong to plan {planId}");
        }

        for (int i = 0; i < orderedStageIds.Count; i++)
        {
            var stage = stageMap[orderedStageIds[i]];
            stage.Sequence = i + 1;
            stage.IsManualOverride = true;
        }

        await db.SaveChangesAsync(ct);
        var full = await LoadFullPlanAsync(p => p.Id == planId, ct);
        return MapToDto(full!);
    }

    public async Task<ReleasePlanDto> MoveItemAsync(Guid planId, Guid itemId, Guid targetStageId, CancellationToken ct = default)
    {
        var item = await db.StageItems
            .Include(i => i.Stage)
            .FirstOrDefaultAsync(i => i.Id == itemId && i.Stage.PlanId == planId, ct)
            ?? throw new InvalidOperationException($"Item {itemId} not found in plan {planId}");

        var targetStage = await db.ReleaseStages
            .FirstOrDefaultAsync(s => s.Id == targetStageId && s.PlanId == planId, ct)
            ?? throw new InvalidOperationException($"Stage {targetStageId} not found in plan {planId}");

        item.StageId = targetStageId;
        targetStage.IsManualOverride = true;

        await db.SaveChangesAsync(ct);
        var full = await LoadFullPlanAsync(p => p.Id == planId, ct);
        return MapToDto(full!);
    }

    public async Task<ReleasePlanDto> AddItemAsync(Guid planId, Guid stageId, Guid mrId, CancellationToken ct = default)
    {
        var stage = await db.ReleaseStages
            .FirstOrDefaultAsync(s => s.Id == stageId && s.PlanId == planId, ct)
            ?? throw new InvalidOperationException($"Stage {stageId} not found in plan {planId}");

        var mrExists = await db.MergeRequests.AnyAsync(mr => mr.Id == mrId, ct);
        if (!mrExists)
            throw new InvalidOperationException($"MergeRequest {mrId} not found");

        db.StageItems.Add(new StageItem
        {
            Id = Guid.NewGuid(),
            StageId = stageId,
            MergeRequestId = mrId,
            ManualInclusion = true
        });
        stage.IsManualOverride = true;

        await db.SaveChangesAsync(ct);
        var full = await LoadFullPlanAsync(p => p.Id == planId, ct);
        return MapToDto(full!);
    }

    public async Task<ReleasePlanDto> RemoveItemAsync(Guid planId, Guid itemId, CancellationToken ct = default)
    {
        var item = await db.StageItems
            .Include(i => i.Stage)
            .FirstOrDefaultAsync(i => i.Id == itemId && i.Stage.PlanId == planId, ct)
            ?? throw new InvalidOperationException($"Item {itemId} not found in plan {planId}");

        db.StageItems.Remove(item);
        await db.SaveChangesAsync(ct);
        var full = await LoadFullPlanAsync(p => p.Id == planId, ct);
        return MapToDto(full!);
    }

    public async Task<ReleasePlanDto> ImportFromYamlAsync(string yaml, bool force = false, CancellationToken ct = default)
    {
        var model = YamlReleasePlanSerializer.Deserialize(yaml);
        var hash = YamlReleasePlanSerializer.ComputeHash(yaml);

        var plan = new ReleasePlan
        {
            Id = Guid.NewGuid(),
            Name = model.Name,
            Version = model.Version,
            IsActive = true,
            AutoGenerated = false,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
            YamlHash = hash
        };

        plan.Stages = [];
        foreach (var stageModel in model.Stages.OrderBy(s => s.Seq))
        {
            var stage = new ReleaseStage
            {
                Id = Guid.NewGuid(),
                PlanId = plan.Id,
                Sequence = stageModel.Seq,
                Name = stageModel.Name,
                IsManualOverride = false,
                Items = []
            };

            foreach (var itemModel in stageModel.Items)
            {
                var (connName, projectPath, iid) = ParseMrId(itemModel.MrId);
                var repo = await db.Repositories
                    .Include(r => r.Connection)
                    .FirstOrDefaultAsync(r => r.Connection.Name == connName && r.ExternalId == projectPath, ct);

                if (repo is null)
                {
                    if (!force) throw new InvalidOperationException($"Repository not found for mr_id: {itemModel.MrId}");
                    continue;
                }

                var mr = await db.MergeRequests.FirstOrDefaultAsync(m => m.RepositoryId == repo.Id && m.ExternalId == iid, ct);
                if (mr is null)
                {
                    if (!force) throw new InvalidOperationException($"MR not found: {itemModel.MrId}");
                    continue;
                }

                stage.Items.Add(new StageItem { Id = Guid.NewGuid(), StageId = stage.Id, MergeRequestId = mr.Id, ManualInclusion = true });
            }

            plan.Stages.Add(stage);
        }

        // Deactivate previous plans
        await db.ReleasePlans.Where(p => p.IsActive).ExecuteUpdateAsync(s => s.SetProperty(p => p.IsActive, false), ct);
        db.ReleasePlans.Add(plan);
        await db.SaveChangesAsync(ct);

        var full = await LoadFullPlanAsync(p => p.Id == plan.Id, ct);
        return MapToDto(full!);
    }

    public async Task<string> ExportToYamlAsync(Guid planId, CancellationToken ct = default)
    {
        var plan = await db.ReleasePlans
            .Include(p => p.Stages).ThenInclude(s => s.Items)
                .ThenInclude(i => i.MergeRequest)
                    .ThenInclude(mr => mr.Repository).ThenInclude(r => r.Connection)
            .Include(p => p.Stages).ThenInclude(s => s.Items)
                .ThenInclude(i => i.MergeRequest)
                    .ThenInclude(mr => mr.Task)
            .FirstOrDefaultAsync(p => p.Id == planId, ct)
            ?? throw new InvalidOperationException($"Plan {planId} not found");

        var model = new YamlReleasePlanModel
        {
            Name = plan.Name,
            Version = plan.Version,
            Created = plan.CreatedAt.ToString("O"),
            Stages = plan.Stages.OrderBy(s => s.Sequence).Select(s => new YamlStageModel
            {
                Seq = s.Sequence,
                Name = s.Name,
                Items = s.Items.Select(i => new YamlStageItemModel
                {
                    MrId = $"{i.MergeRequest.Repository.Connection.Name}:{i.MergeRequest.Repository.ExternalId}!{i.MergeRequest.ExternalId}",
                    Task = i.MergeRequest.Task?.ExternalId
                }).ToList()
            }).ToList()
        };

        return YamlReleasePlanSerializer.Serialize(model);
    }

    private async Task<ReleasePlan?> LoadFullPlanAsync(
        System.Linq.Expressions.Expression<Func<ReleasePlan, bool>> predicate,
        CancellationToken ct)
    {
        return await db.ReleasePlans
            .Include(p => p.Stages).ThenInclude(s => s.Items)
                .ThenInclude(i => i.MergeRequest).ThenInclude(mr => mr.Repository)
                    .ThenInclude(r => r.Connection)
            .Include(p => p.Stages).ThenInclude(s => s.Items)
                .ThenInclude(i => i.MergeRequest).ThenInclude(mr => mr.Task)
            .AsNoTracking()
            .FirstOrDefaultAsync(predicate, ct);
    }

    private static (string connName, string projectPath, string iid) ParseMrId(string mrId)
    {
        var colonIdx = mrId.IndexOf(':');
        var bangIdx = mrId.LastIndexOf('!');
        if (colonIdx < 0 || bangIdx < 0) throw new FormatException($"Invalid mr_id format: {mrId}");
        return (mrId[..colonIdx], mrId[(colonIdx + 1)..bangIdx], mrId[(bangIdx + 1)..]);
    }

    private static List<List<Guid>> BuildStages(List<MergeRequest> mrs)
    {
        var ids = mrs.Select(mr => mr.Id).ToHashSet();
        var inDegree = ids.ToDictionary(id => id, _ => 0);
        var edges = new Dictionary<Guid, List<Guid>>();
        foreach (var id in ids) edges[id] = [];

        foreach (var mr in mrs)
        {
            foreach (var dep in mr.Task?.Dependencies ?? [])
            {
                var depMr = mrs.FirstOrDefault(m => m.TaskId == dep.DependsOnTaskId);
                if (depMr is not null && ids.Contains(depMr.Id))
                {
                    edges[depMr.Id].Add(mr.Id);
                    inDegree[mr.Id]++;
                }
            }

            foreach (var rs in mr.Repository.RepositoryStacks)
            {
                foreach (var stackDep in rs.Stack.DependentOn.Where(d => d.Type == StackDependencyType.Hard))
                {
                    var depMrs = mrs.Where(m => m.Repository.RepositoryStacks.Any(s => s.StackId == stackDep.ToStackId));
                    foreach (var depMr in depMrs.Where(m => ids.Contains(m.Id)))
                    {
                        edges[depMr.Id].Add(mr.Id);
                        inDegree[mr.Id]++;
                    }
                }
            }
        }

        // Kahn's topological sort
        var result = new List<List<Guid>>();
        var queue = new Queue<Guid>(inDegree.Where(kv => kv.Value == 0).Select(kv => kv.Key));

        while (queue.Count > 0)
        {
            var level = new List<Guid>();
            int levelSize = queue.Count;
            for (int i = 0; i < levelSize; i++)
            {
                var node = queue.Dequeue();
                level.Add(node);
                foreach (var next in edges[node])
                {
                    if (--inDegree[next] == 0)
                        queue.Enqueue(next);
                }
            }
            result.Add(level);
        }

        // Append any remaining nodes (cycles treated as last stage)
        var processed = result.SelectMany(l => l).ToHashSet();
        var remaining = ids.Except(processed).ToList();
        if (remaining.Count > 0)
            result.Add(remaining);

        return result;
    }

    private async Task<ReleasePlan> PersistPlanAsync(List<List<Guid>> stages, CancellationToken ct)
    {
        var plan = new ReleasePlan
        {
            Id = Guid.NewGuid(),
            Name = "Auto plan",
            Version = "auto",
            IsActive = true,
            AutoGenerated = true,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        plan.Stages = stages.Select((level, idx) => new ReleaseStage
        {
            Id = Guid.NewGuid(),
            PlanId = plan.Id,
            Sequence = idx + 1,
            Items = level.Select(mrId => new StageItem
            {
                Id = Guid.NewGuid(),
                MergeRequestId = mrId
            }).ToList()
        }).ToList();

        // Atomic swap: add new, deactivate old in one SaveChanges call
        await db.ReleasePlans
            .Where(p => p.IsActive && p.AutoGenerated)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.IsActive, false), ct);

        db.ReleasePlans.Add(plan);
        await db.SaveChangesAsync(ct);

        var full = await db.ReleasePlans
            .Include(p => p.Stages).ThenInclude(s => s.Items)
                .ThenInclude(i => i.MergeRequest).ThenInclude(mr => mr.Repository)
                    .ThenInclude(r => r.Connection)
            .Include(p => p.Stages).ThenInclude(s => s.Items)
                .ThenInclude(i => i.MergeRequest).ThenInclude(mr => mr.Task)
            .AsNoTracking()
            .FirstAsync(p => p.Id == plan.Id, ct);
        return full;
    }

    private static ReleasePlanDto MapToDto(ReleasePlan plan) => new(
        plan.Id, plan.Name, plan.Version, plan.IsActive, plan.AutoGenerated,
        plan.CreatedAt, plan.UpdatedAt,
        plan.Stages.OrderBy(s => s.Sequence).Select(s => new ReleaseStageDto(
            s.Id, s.Sequence, s.Name, s.IsManualOverride,
            s.Items.Select(i => new StageItemDto(
                i.Id,
                i.MergeRequestId,
                i.ManualInclusion,
                i.MergeRequest?.ExternalId ?? "",
                i.MergeRequest?.SourceBranch ?? "",
                i.MergeRequest?.TargetBranch ?? "",
                i.MergeRequest?.Repository?.Name ?? "",
                i.MergeRequest?.Task?.ExternalId,
                i.MergeRequest?.Status.ToString() ?? "")).ToList()
        )).ToList());
}
