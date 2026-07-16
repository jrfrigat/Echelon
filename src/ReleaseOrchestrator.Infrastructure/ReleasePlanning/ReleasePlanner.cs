using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ReleaseOrchestrator.Application.DTOs;
using ReleaseOrchestrator.Application.Exceptions;
using ReleaseOrchestrator.Application.ReleasePlanning;
using ReleaseOrchestrator.Application.Services;
using ReleaseOrchestrator.Core.Entities;
using ReleaseOrchestrator.Core.Enums;
using ReleaseOrchestrator.Infrastructure.Persistence;

namespace ReleaseOrchestrator.Infrastructure.ReleasePlanning;

public class ReleasePlanner(AppDbContext db, TimeProvider clock, ILogger<ReleasePlanner> logger) : IReleasePlannerService
{
    public async Task<ReleasePlanDto> RecalculateAsync(CancellationToken ct = default)
    {
        // AsSplitQuery: three collection Includes in one query multiply rows together,
        // which at the documented 10k merge requests means millions of rows on the wire.
        // The OrderBy is what makes the plan reproducible — ties inside a stage follow it.
        var readyMrs = await db.MergeRequests
            .Include(mr => mr.Task)
                .ThenInclude(t => t!.Dependencies)
            .Include(mr => mr.Repository)
                .ThenInclude(r => r.RepositoryStacks)
                    .ThenInclude(rs => rs.Stack)
                        .ThenInclude(s => s.DependentOn)
            .Where(mr => mr.Status == MergeRequestStatus.ReadyForDeploy)
            .OrderBy(mr => mr.CreatedAt).ThenBy(mr => mr.Id)
            .AsSplitQuery()
            .AsNoTracking()
            .ToListAsync(ct);

        var graph = ReleasePlanGraph.Build(readyMrs);

        if (graph.Conflicts.Count > 0)
            logger.LogWarning(
                "Release plan built with {ConflictCount} dropped dependency link(s); the stage order violates them.",
                graph.Conflicts.Count);

        var full = await PersistPlanAsync(graph, ct);
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
            ?? throw new NotFoundException($"Plan {planId} not found");

        var stageMap = plan.Stages.ToDictionary(s => s.Id);

        // Requiring the complete set is what keeps Sequence unique: a partial reorder
        // renumbers some stages from 1 while the rest keep their old numbers.
        if (orderedStageIds.Count != stageMap.Count || orderedStageIds.Distinct().Count() != orderedStageIds.Count)
            throw new DomainValidationException(
                $"Reorder must list each of the plan's {stageMap.Count} stages exactly once; got {orderedStageIds.Count}.");

        foreach (var stageId in orderedStageIds)
            if (!stageMap.ContainsKey(stageId))
                throw new DomainValidationException($"Stage {stageId} does not belong to plan {planId}");

        for (int i = 0; i < orderedStageIds.Count; i++)
        {
            var stage = stageMap[orderedStageIds[i]];
            stage.Sequence = i + 1;
            stage.IsManualOverride = true;
        }

        plan.UpdatedAt = clock.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(ct);
        return MapToDto((await LoadFullPlanAsync(p => p.Id == planId, ct))!);
    }

    public async Task<ReleasePlanDto> MoveItemAsync(Guid planId, Guid itemId, Guid targetStageId, CancellationToken ct = default)
    {
        var item = await db.StageItems
            .Include(i => i.Stage)
            .FirstOrDefaultAsync(i => i.Id == itemId && i.Stage.PlanId == planId, ct)
            ?? throw new NotFoundException($"Item {itemId} not found in plan {planId}");

        var targetStage = await db.ReleaseStages
            .FirstOrDefaultAsync(s => s.Id == targetStageId && s.PlanId == planId, ct)
            ?? throw new NotFoundException($"Stage {targetStageId} not found in plan {planId}");

        item.StageId = targetStageId;
        targetStage.IsManualOverride = true;

        await TouchPlanAsync(planId, ct);
        await db.SaveChangesAsync(ct);
        return MapToDto((await LoadFullPlanAsync(p => p.Id == planId, ct))!);
    }

    public async Task<ReleasePlanDto> AddItemAsync(Guid planId, Guid stageId, Guid mrId, CancellationToken ct = default)
    {
        var stage = await db.ReleaseStages
            .FirstOrDefaultAsync(s => s.Id == stageId && s.PlanId == planId, ct)
            ?? throw new NotFoundException($"Stage {stageId} not found in plan {planId}");

        if (!await db.MergeRequests.AnyAsync(mr => mr.Id == mrId, ct))
            throw new NotFoundException($"MergeRequest {mrId} not found");

        db.StageItems.Add(new StageItem
        {
            Id = Guid.NewGuid(),
            StageId = stageId,
            MergeRequestId = mrId,
            ManualInclusion = true
        });
        stage.IsManualOverride = true;

        await TouchPlanAsync(planId, ct);
        await db.SaveChangesAsync(ct);
        return MapToDto((await LoadFullPlanAsync(p => p.Id == planId, ct))!);
    }

    public async Task<ReleasePlanDto> RemoveItemAsync(Guid planId, Guid itemId, CancellationToken ct = default)
    {
        var item = await db.StageItems
            .Include(i => i.Stage)
            .FirstOrDefaultAsync(i => i.Id == itemId && i.Stage.PlanId == planId, ct)
            ?? throw new NotFoundException($"Item {itemId} not found in plan {planId}");

        db.StageItems.Remove(item);
        await TouchPlanAsync(planId, ct);
        await db.SaveChangesAsync(ct);
        return MapToDto((await LoadFullPlanAsync(p => p.Id == planId, ct))!);
    }

    public async Task<ReleasePlanDto> ImportFromYamlAsync(string yaml, bool force = false, CancellationToken ct = default)
    {
        var model = YamlReleasePlanSerializer.Deserialize(yaml);
        var now = clock.GetUtcNow().UtcDateTime;

        var plan = new ReleasePlan
        {
            Id = Guid.NewGuid(),
            Name = model.Name,
            Version = model.Version,
            IsActive = true,
            AutoGenerated = false,
            CreatedAt = now,
            UpdatedAt = now,
            YamlHash = YamlReleasePlanSerializer.ComputeHash(yaml),
            Stages = []
        };

        var resolved = await ResolveYamlItemsAsync(model, force, ct);

        foreach (var stageModel in model.Stages.OrderBy(s => s.Seq))
        {
            var stage = new ReleaseStage
            {
                Id = Guid.NewGuid(),
                PlanId = plan.Id,
                Sequence = stageModel.Seq,
                Name = stageModel.Name,
                IsManualOverride = true,
                Items = []
            };

            foreach (var itemModel in stageModel.Items)
                if (resolved.TryGetValue(itemModel.MrId, out var mrId))
                    stage.Items.Add(new StageItem
                    {
                        Id = Guid.NewGuid(),
                        StageId = stage.Id,
                        MergeRequestId = mrId,
                        ManualInclusion = true
                    });

            plan.Stages.Add(stage);
        }

        await ValidateHardDependenciesAsync(plan, force, ct);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.ReleasePlans
            .Where(p => p.IsActive)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.IsActive, false), ct);
        db.ReleasePlans.Add(plan);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        return MapToDto((await LoadFullPlanAsync(p => p.Id == plan.Id, ct))!);
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
            .AsSplitQuery()
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == planId, ct)
            ?? throw new NotFoundException($"Plan {planId} not found");

        var model = new YamlReleasePlanModel
        {
            Name = plan.Name,
            Version = plan.Version,
            Created = plan.CreatedAt.ToString("O"),
            Stages = plan.Stages.OrderBy(s => s.Sequence).Select(s => new YamlStageModel
            {
                Seq = s.Sequence,
                Name = s.Name,
                Items = s.Items
                    .OrderBy(i => i.MergeRequest.Repository.Name).ThenBy(i => i.MergeRequest.ExternalId)
                    .Select(i => new YamlStageItemModel
                    {
                        MrId = FormatMrId(i.MergeRequest),
                        Task = i.MergeRequest.Task?.ExternalId
                    }).ToList()
            }).ToList(),
            ManualOverrides = plan.Stages
                .Where(s => s.IsManualOverride)
                .OrderBy(s => s.Sequence)
                .Select(s => new YamlManualOverride
                {
                    Type = "stage_edited",
                    Reason = $"Stage {s.Sequence} was edited manually"
                }).ToList()
        };

        return YamlReleasePlanSerializer.Serialize(model);
    }

    /// <summary>Resolves every <c>mr_id</c> in one pair of queries rather than two per item.</summary>
    private async Task<Dictionary<string, Guid>> ResolveYamlItemsAsync(YamlReleasePlanModel model, bool force, CancellationToken ct)
    {
        var parsed = model.Stages
            .SelectMany(s => s.Items)
            .Select(i => i.MrId)
            .Distinct()
            .Select(id => (Raw: id, Parsed: ParseMrId(id)))
            .ToList();

        var connNames = parsed.Select(p => p.Parsed.connName).Distinct().ToList();
        var projectPaths = parsed.Select(p => p.Parsed.projectPath).Distinct().ToList();
        var iids = parsed.Select(p => p.Parsed.iid).Distinct().ToList();

        var repos = await db.Repositories
            .Include(r => r.Connection)
            .Where(r => connNames.Contains(r.Connection.Name) && projectPaths.Contains(r.ExternalId))
            .Select(r => new { r.Id, ConnName = r.Connection.Name, r.ExternalId })
            .AsNoTracking()
            .ToListAsync(ct);

        var repoByKey = repos.ToDictionary(r => (r.ConnName, r.ExternalId), r => r.Id);
        var repoIds = repos.Select(r => r.Id).ToList();

        var mrs = await db.MergeRequests
            .Where(m => repoIds.Contains(m.RepositoryId) && iids.Contains(m.ExternalId))
            .Select(m => new { m.Id, m.RepositoryId, m.ExternalId })
            .AsNoTracking()
            .ToListAsync(ct);

        var mrByKey = mrs.ToDictionary(m => (m.RepositoryId, m.ExternalId), m => m.Id);

        var resolved = new Dictionary<string, Guid>();
        foreach (var (raw, (connName, projectPath, iid)) in parsed)
        {
            if (!repoByKey.TryGetValue((connName, projectPath), out var repoId))
            {
                if (!force) throw new DomainValidationException($"Repository not found for mr_id: {raw}");
                continue;
            }

            if (!mrByKey.TryGetValue((repoId, iid), out var mrId))
            {
                if (!force) throw new DomainValidationException($"MR not found: {raw}");
                continue;
            }

            resolved[raw] = mrId;
        }

        return resolved;
    }

    /// <summary>
    /// README §6.2: an import must not violate a hard dependency. Rebuilds the graph for
    /// the imported merge requests and checks the YAML stage order against it.
    /// </summary>
    private async Task ValidateHardDependenciesAsync(ReleasePlan plan, bool force, CancellationToken ct)
    {
        if (force) return;

        var mrIds = plan.Stages.SelectMany(s => s.Items).Select(i => i.MergeRequestId).ToList();
        if (mrIds.Count == 0) return;

        var mrs = await db.MergeRequests
            .Include(mr => mr.Task).ThenInclude(t => t!.Dependencies)
            .Include(mr => mr.Repository).ThenInclude(r => r.RepositoryStacks)
                .ThenInclude(rs => rs.Stack).ThenInclude(s => s.DependentOn)
            .Where(mr => mrIds.Contains(mr.Id))
            .OrderBy(mr => mr.CreatedAt).ThenBy(mr => mr.Id)
            .AsSplitQuery()
            .AsNoTracking()
            .ToListAsync(ct);

        var stageOf = plan.Stages
            .SelectMany(s => s.Items.Select(i => (i.MergeRequestId, s.Sequence)))
            .ToDictionary(x => x.MergeRequestId, x => x.Sequence);

        // Reuse the planner's own edge derivation so import and recalculation can never
        // disagree about what a hard dependency is.
        var violations = ReleasePlanGraph.MandatoryEdges(mrs)
            .Where(e => stageOf.TryGetValue(e.FromMrId, out var predSeq)
                        && stageOf.TryGetValue(e.ToMrId, out var seq)
                        && predSeq >= seq)
            .Select(e => $"{Describe(mrs, e.FromMrId)} must deploy before {Describe(mrs, e.ToMrId)}, "
                         + $"but sits in stage {stageOf[e.FromMrId]} against stage {stageOf[e.ToMrId]}")
            .Distinct()
            .ToList();

        if (violations.Count > 0)
            throw new DomainValidationException(
                "Imported plan violates hard dependencies; pass force=true to override. "
                + string.Join("; ", violations.Take(10)));
    }

    private static string Describe(IEnumerable<MergeRequest> mrs, Guid mrId)
    {
        var mr = mrs.First(m => m.Id == mrId);
        return $"{mr.Repository.Name}!{mr.ExternalId}";
    }

    private async Task TouchPlanAsync(Guid planId, CancellationToken ct)
    {
        var plan = await db.ReleasePlans.FirstOrDefaultAsync(p => p.Id == planId, ct);
        if (plan is not null) plan.UpdatedAt = clock.GetUtcNow().UtcDateTime;
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
            .AsSplitQuery()
            .AsNoTracking()
            .FirstOrDefaultAsync(predicate, ct);
    }

    private static string FormatMrId(MergeRequest mr) =>
        $"{mr.Repository.Connection.Name}:{mr.Repository.ExternalId}!{mr.ExternalId}";

    private static (string connName, string projectPath, string iid) ParseMrId(string mrId)
    {
        if (string.IsNullOrWhiteSpace(mrId))
            throw new DomainValidationException("mr_id is required (expected 'connection:project/path!iid')");

        var colonIdx = mrId.IndexOf(':');
        var bangIdx = mrId.LastIndexOf('!');

        // colonIdx < bangIdx matters: "a!b:c" would otherwise slice a negative-length range.
        if (colonIdx <= 0 || bangIdx <= colonIdx || bangIdx == mrId.Length - 1)
            throw new DomainValidationException($"Invalid mr_id format: '{mrId}' (expected 'connection:project/path!iid')");

        return (mrId[..colonIdx], mrId[(colonIdx + 1)..bangIdx], mrId[(bangIdx + 1)..]);
    }

    private async Task<ReleasePlan> PersistPlanAsync(PlanGraphResult graph, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;

        // An operator's imported plan outranks an automatic one: recalculation stores its
        // result inactive rather than overwriting deliberate manual work (README §6.1).
        var manualPlanActive = await db.ReleasePlans.AnyAsync(p => p.IsActive && !p.AutoGenerated, ct);

        var plan = new ReleasePlan
        {
            Id = Guid.NewGuid(),
            Name = $"Auto plan {now:yyyy-MM-dd HH:mm} UTC",
            Version = now.ToString("yyyyMMddHHmmss"),
            IsActive = !manualPlanActive,
            AutoGenerated = true,
            CreatedAt = now,
            UpdatedAt = now,
            ConflictsJson = graph.Conflicts.Count == 0 ? null : JsonSerializer.Serialize(graph.Conflicts)
        };

        plan.Stages = graph.Stages.Select((level, idx) => new ReleaseStage
        {
            Id = Guid.NewGuid(),
            PlanId = plan.Id,
            Sequence = idx + 1,
            Name = $"Stage {idx + 1}",
            Items = level.Select(mrId => new StageItem
            {
                Id = Guid.NewGuid(),
                MergeRequestId = mrId
            }).ToList()
        }).ToList();

        if (manualPlanActive)
            logger.LogInformation(
                "An imported plan is active; storing recalculated plan {PlanId} as inactive so manual edits survive.",
                plan.Id);

        // One transaction: without it a crash between the two statements leaves no active
        // plan at all, and two concurrent recalculations both deactivate then both insert.
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        if (!manualPlanActive)
            await db.ReleasePlans
                .Where(p => p.IsActive)
                .ExecuteUpdateAsync(s => s.SetProperty(p => p.IsActive, false), ct);

        db.ReleasePlans.Add(plan);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);

        return (await LoadFullPlanAsync(p => p.Id == plan.Id, ct))!;
    }

    private static ReleasePlanDto MapToDto(ReleasePlan plan) => new(
        plan.Id, plan.Name, plan.Version, plan.IsActive, plan.AutoGenerated,
        plan.CreatedAt, plan.UpdatedAt,
        plan.Stages.OrderBy(s => s.Sequence).Select(s => new ReleaseStageDto(
            s.Id, s.Sequence, s.Name, s.IsManualOverride,
            s.Items
                .OrderBy(i => i.MergeRequest?.Repository?.Name).ThenBy(i => i.MergeRequest?.ExternalId)
                .Select(i => new StageItemDto(
                    i.Id,
                    i.MergeRequestId,
                    i.ManualInclusion,
                    i.MergeRequest?.ExternalId ?? "",
                    i.MergeRequest?.SourceBranch ?? "",
                    i.MergeRequest?.TargetBranch ?? "",
                    i.MergeRequest?.Repository?.Name ?? "",
                    i.MergeRequest?.Task?.ExternalId,
                    i.MergeRequest?.Status.ToString() ?? "")).ToList()
        )).ToList(),
        DeserializeConflicts(plan.ConflictsJson));

    private static IReadOnlyList<PlanConflictDto> DeserializeConflicts(string? json)
    {
        if (string.IsNullOrEmpty(json)) return [];

        var conflicts = JsonSerializer.Deserialize<List<PlanConflict>>(json) ?? [];
        return conflicts
            .Select(c => new PlanConflictDto(c.DroppedEdgeKind.ToString(), c.FromMrId, c.ToMrId, c.Reason))
            .ToList();
    }
}
