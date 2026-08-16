using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Echelon.Application.DTOs;
using Echelon.Application.Exceptions;
using Echelon.Application.ReleasePlanning;
using Echelon.Application.Services;
using Echelon.Core.Enums;
using Echelon.Infrastructure.Persistence;
using Echelon.Infrastructure.Persistence.Models;
using YamlDotNet.Serialization;

namespace Echelon.Infrastructure.ReleasePlanning;

/// <summary>
/// Builds and reads per-task rollout plans by projecting the atlas onto one target task's
/// dependency closure (docs/issues/006-per-task-planning.md).
/// </summary>
/// <remarks>
/// The plan of record is persisted as <see cref="RolloutPlan"/> + its task nodes and items; the
/// execution waves shown to an operator are derived from the same pure graph, so the two cannot
/// disagree about ordering. Operator edits survive a recalculation: they are stored as deltas
/// against the task and replayed on every build, which is what a projection of the atlas requires.
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
    /// <remarks>
    /// Reads the stored nodes, waves and conflicts rather than recomputing them. It used to take the
    /// metadata from the row and the CONTENT from a fresh computation, which meant version N was
    /// displayed with content version N never had as soon as the atlas moved - and the launch, which
    /// reads the stored rows, would then deploy an order the operator had not seen. A plan version has
    /// to mean one thing; keeping it current is what recalculation is for, and every ingestion event
    /// triggers one.
    /// </remarks>
    public async Task<RolloutPlanDto?> GetActivePlanAsync(Guid taskId, CancellationToken ct = default)
    {
        var plan = await db.RolloutPlans
            .Include(p => p.Nodes).ThenInclude(n => n.Items)
            .AsSplitQuery()
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.TargetTaskId == taskId && p.IsActive, ct);

        return plan is null ? null : await BuildStoredDtoAsync(plan, ct);
    }

    /// <inheritdoc/>
    public Task<RolloutPlanDto> RecalculateAsync(Guid taskId, ActorRef? actor, CancellationToken ct = default) =>
        StoreAsync(taskId, actor, PlanSource.Generated, yamlHash: null, ct);

    /// <summary>
    /// Derives the plan for a task and stores it as the new active version.
    /// </summary>
    /// <param name="source">How this version came about; an import records itself as such.</param>
    /// <param name="yamlHash">The imported document's hash, or null when nobody imported one.</param>
    /// <remarks>
    /// The single writer. Recalculation and import both land here rather than each assembling a plan,
    /// which is what keeps 006 §1 true at the storage layer as well as in the derivation: an imported
    /// plan is a derived plan whose deltas were written first.
    /// </remarks>
    private async Task<RolloutPlanDto> StoreAsync(
        Guid taskId, ActorRef? actor, PlanSource source, string? yamlHash, CancellationToken ct)
    {
        // Stamped before the read: anything committed before this instant is in the plan.
        var snapshotStartedAt = clock.GetUtcNow().UtcDateTime;

        var target = await db.Tasks.FirstOrDefaultAsync(t => t.Id == taskId, ct)
            ?? throw new NotFoundException($"Task {taskId} not found");

        var computed = await ComputeAsync(taskId, ct);
        var now = clock.GetUtcNow().UtcDateTime;

        // One active plan per task: deactivate the previous one and insert in a single transaction,
        // so a crash between the two never leaves the task with no active plan and two concurrent
        // recalculations cannot both leave one active (the filtered unique index is the backstop).
        //
        // Joins an ambient transaction when there is one. Import opens its own, because writing the
        // deltas and building the plan that reflects them is one act: committing the deltas alone
        // would apply the import at the next ingestion event, from an endpoint that had just reported
        // failure.
        var ownsTransaction = db.Database.CurrentTransaction is null;
        await using var tx = ownsTransaction ? await db.Database.BeginTransactionAsync(ct) : null;

        // Read inside the transaction, together with the deactivation that serialises concurrent
        // rebuilds of this task. Max over the task's own history rather than a count: superseded rows
        // are never deleted today, but a retention that removed them must not restart the numbering.
        var previousVersion = await db.RolloutPlans
            .Where(p => p.TargetTaskId == taskId)
            .MaxAsync(p => (int?)p.Version, ct) ?? 0;

        var plan = new RolloutPlan
        {
            Id = Guid.NewGuid(),
            TargetTaskId = taskId,
            Version = previousVersion + 1,
            Source = source,
            Status = PlanStatus.Ready,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
            SnapshotStartedAt = snapshotStartedAt,
            ConflictsJson = computed.Graph.Conflicts.Count == 0 ? null : JsonSerializer.Serialize(computed.Graph.Conflicts),
            ContentHash = ComputeContentHash(computed),
            YamlHash = yamlHash,
            // Null for the recalculation consumer, which rebuilds every active plan on every
            // ingestion event. Stamped inside the same transaction as the plan below, so authorship
            // cannot commit without the version it describes.
            CreatedByOid = actor?.Oid,
            CreatedByKind = actor?.Kind,
            CreatedByName = actor?.DisplayName,
            Nodes = []
        };

        var waveOf = WavesOf(computed.Graph);

        // A node per closure task -- including tasks with no merge requests, so the tree shows the
        // whole closure. Items hang off their task's node, each carrying the wave it was placed in:
        // the plan of record has to record its own order, or the launch has to guess it again.
        foreach (var closureTaskId in computed.Closure)
        {
            var dependsOn = computed.Adjacency.TryGetValue(closureTaskId, out var d)
                ? d.Where(computed.Closure.Contains).OrderBy(id => id).ToList()
                : [];

            var node = new PlanTaskNode
            {
                Id = Guid.NewGuid(),
                RolloutPlanId = plan.Id,
                TaskId = closureTaskId,
                DependsOnTaskIdsJson = dependsOn.Count == 0 ? null : JsonSerializer.Serialize(dependsOn),
                Items = []
            };

            foreach (var mr in computed.PlanMrs.Where(m => m.TaskId == closureTaskId))
                node.Items.Add(new PlanItem
                {
                    Id = Guid.NewGuid(),
                    PlanTaskNodeId = node.Id,
                    MergeRequestId = mr.Id,
                    Wave = waveOf.GetValueOrDefault(mr.Id),
                    // Recorded so the stored plan says which rows are here because someone asked,
                    // rather than because the derivation chose them. Reading it off the delta at
                    // display time would answer for the CURRENT deltas, not for this version.
                    ManualInclusion = computed.ManuallyIncluded.Contains(mr.Id)
                });

            plan.Nodes.Add(node);
        }

        await db.RolloutPlans
            .Where(p => p.TargetTaskId == taskId && p.IsActive)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.IsActive, false), ct);
        db.RolloutPlans.Add(plan);
        await db.SaveChangesAsync(ct);
        if (tx is not null) await tx.CommitAsync(ct);

        if (computed.Graph.Conflicts.Count > 0)
            logger.LogWarning(
                "Rollout plan for task {Task} built with {Count} dropped dependency link(s); the order violates them.",
                target.ExternalId, computed.Graph.Conflicts.Count);

        return await BuildStoredDtoAsync(plan, ct);
    }

    /// <inheritdoc/>
    public Task<PlanImportDto> ValidatePlanAsync(Guid taskId, string document, CancellationToken ct = default) =>
        ReconcileAsync(taskId, document, force: false, actor: null, apply: false, ct);

    /// <inheritdoc/>
    public Task<PlanImportDto> ImportPlanAsync(
        Guid taskId, string document, bool force, ActorRef? actor, CancellationToken ct = default) =>
        ReconcileAsync(taskId, document, force, actor, apply: true, ct);

    /// <summary>
    /// Reconciles a plan document against the derivation, and optionally stores the result.
    /// </summary>
    /// <param name="apply">False for validate: the same work, nothing written.</param>
    /// <remarks>
    /// <para>
    /// One method for both endpoints, because a validate that checked anything different from what
    /// import does would be worse than no validate at all: it would say yes and then the import would
    /// say no, or - far worse - say nothing and produce a different order.
    /// </para>
    /// <para>
    /// A document states one thing: which wave each merge request deploys in. Everything else it
    /// carries is checked for AGREEMENT and never applied. Which tasks and merge requests a plan
    /// covers comes from the atlas, so a document that disagrees is rejected rather than obeyed - a
    /// plan is a projection, and an import that could add work to one would make it something else.
    /// </para>
    /// </remarks>
    private async Task<PlanImportDto> ReconcileAsync(
        Guid taskId, string document, bool force, ActorRef? actor, bool apply, CancellationToken ct)
    {
        var parsed = PlanDocumentReader.Read(document);
        if (!parsed.IsValid) return new PlanImportDto(false, parsed.Errors, [], null);

        var target = await db.Tasks
            .Where(t => t.Id == taskId)
            .Select(t => new { t.Id, t.ExternalId })
            .AsNoTracking()
            .FirstOrDefaultAsync(ct)
            ?? throw new NotFoundException($"Task {taskId} not found");

        var computed = await ComputeAsync(taskId, ct);
        var keys = await ResolveKeysAsync(computed, ct);

        var errors = new List<string>();
        var doc = parsed.Document!;

        if (!string.Equals(doc.TargetTaskKey, target.ExternalId, StringComparison.Ordinal))
            errors.Add(
                $"The document rolls out '{doc.TargetTaskKey}', but it was posted to '{target.ExternalId}'. "
                + "Import a plan to the task it targets.");

        var waveOf = ResolveWaves(doc, computed, keys, errors);
        if (errors.Count > 0) return new PlanImportDto(false, errors, [], null);

        // 0-based, because that is the stage numbering ViolatedBy compares in. Waves are 1-based
        // everywhere an operator sees them.
        var stageOf = waveOf.ToDictionary(kv => kv.Key, kv => kv.Value - 1);
        var violations = ReleasePlanGraph.ViolatedBy(computed.PlanMrs, stageOf)
            .Select(e => new PlanImportViolationDto(
                e.Kind.ToString(),
                keys.MrKeys.GetValueOrDefault(e.FromMrId, e.FromMrId.ToString("D")),
                keys.MrKeys.GetValueOrDefault(e.ToMrId, e.ToMrId.ToString("D"))))
            .ToList();

        // force skips the REFUSAL, never the record. The plan still reports every constraint it
        // breaks, because a plan that deploys against a hard dependency and looks clean is the one
        // outcome this system does not allow (006 §2).
        if (violations.Count > 0 && !force)
            return new PlanImportDto(false, [], violations, null);

        if (!apply) return new PlanImportDto(true, [], violations, null);

        var deltas = PlanWavePinning.Compute(
            computed.PlanMrs,
            ReleasePlanGraph.DerivedEdges(computed.PlanMrs, computed.RuleEdges),
            waveOf);

        // Checked before anything is written: replay the deltas through the real derivation and
        // confirm the waves come back as written. Import is the one path where the deltas are computed
        // rather than authored, so "the derivation agrees" is a claim to verify, not to assume.
        var replayed = WavesOf(ReleasePlanGraph.Build(
            computed.PlanMrs, deltas.Add, deltas.Remove, computed.RuleEdges));

        if (waveOf.Any(kv => replayed.GetValueOrDefault(kv.Key) != kv.Value))
            return new PlanImportDto(
                false,
                ["The requested waves could not be reproduced by the planner, so nothing was saved. "
                 + "This is a defect rather than a problem with the document; please report it."],
                violations,
                null);

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        // Replaced wholesale. The deltas describe one intent - the document just posted - and merging
        // them with the previous import's would leave edges nobody asked for pinning waves nobody
        // wrote.
        await db.PlanOverrides
            .Where(o => o.TaskId == taskId
                        && (o.Kind == PlanOverrideKind.AddEdge || o.Kind == PlanOverrideKind.RemoveEdge))
            .ExecuteDeleteAsync(ct);

        foreach (var (from, to) in deltas.Add) db.PlanOverrides.Add(NewOverride(taskId, PlanOverrideKind.AddEdge, from, to));
        foreach (var (from, to) in deltas.Remove) db.PlanOverrides.Add(NewOverride(taskId, PlanOverrideKind.RemoveEdge, from, to));
        await db.SaveChangesAsync(ct);

        var plan = await StoreAsync(taskId, actor, PlanSource.Imported, HashOf(document), ct);
        await tx.CommitAsync(ct);

        return new PlanImportDto(true, [], violations, plan);
    }

    private static PlanOverride NewOverride(Guid taskId, PlanOverrideKind kind, Guid from, Guid to) =>
        new()
        {
            Id = Guid.NewGuid(),
            TaskId = taskId,
            Kind = kind,
            Payload = JsonSerializer.Serialize(new PlanEdgeOverridePayload(from, to))
        };

    /// <summary>SHA-256 of the document as posted, so an operator can tell whether a plan is still theirs.</summary>
    private static string HashOf(string document) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(document)));

    /// <summary>The keys the document is written in, both ways round.</summary>
    private async Task<PlanKeys> ResolveKeysAsync(Computed computed, CancellationToken ct)
    {
        var taskKeys = (await db.Tasks
                .Where(t => computed.Closure.Contains(t.Id))
                .Select(t => new { t.Id, t.ExternalId })
                .AsNoTracking()
                .ToListAsync(ct))
            .ToDictionary(t => t.Id, t => t.ExternalId);

        var mrIds = computed.PlanMrs.Select(m => m.Id).ToList();
        var mrKeys = (await db.MergeRequests
                .Where(m => mrIds.Contains(m.Id))
                .Select(m => new
                {
                    m.Id,
                    Key = m.Repository.Connection.Name + ":" + m.Repository.ExternalId + "!" + m.ExternalId
                })
                .AsNoTracking()
                .ToListAsync(ct))
            .ToDictionary(m => m.Id, m => m.Key);

        return new PlanKeys(taskKeys, mrKeys);
    }

    /// <summary>The document's keys and the ids they stand for, in both directions.</summary>
    private sealed record PlanKeys(
        IReadOnlyDictionary<Guid, string> TaskKeys, IReadOnlyDictionary<Guid, string> MrKeys);

    /// <summary>
    /// Resolves a document against the derived plan, answering the wave each merge request is to
    /// deploy in - or filling <paramref name="errors"/> and answering nothing usable.
    /// </summary>
    /// <remarks>
    /// Membership is compared in BOTH directions. A document missing a merge request is as wrong as
    /// one inventing a merge request: both mean the author is describing a different plan from the one
    /// they would be overwriting, and applying the overlap would silently import half an intention.
    /// </remarks>
    private static Dictionary<Guid, int> ResolveWaves(
        PlanDocument doc, Computed computed, PlanKeys keys, List<string> errors)
    {
        var taskByKey = keys.TaskKeys.GroupBy(kv => kv.Value, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Key, StringComparer.Ordinal);
        var mrByKey = keys.MrKeys.GroupBy(kv => kv.Value, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Key, StringComparer.Ordinal);

        var taskOfMr = computed.PlanMrs.ToDictionary(m => m.Id, m => m.TaskId);
        var waveOf = new Dictionary<Guid, int>();
        var seenTasks = new HashSet<Guid>();

        foreach (var node in doc.Nodes)
        {
            if (!taskByKey.TryGetValue(node.TaskKey, out var nodeTaskId))
            {
                errors.Add(
                    $"Unknown task '{node.TaskKey}'. This plan covers: {Describe(keys.TaskKeys.Values)}.");
                continue;
            }

            seenTasks.Add(nodeTaskId);

            foreach (var item in node.Items)
            {
                if (!mrByKey.TryGetValue(item.MergeRequestKey, out var mrId))
                {
                    errors.Add(
                        $"Unknown merge request '{item.MergeRequestKey}'. This plan covers: "
                        + $"{Describe(keys.MrKeys.Values)}.");
                    continue;
                }

                // Which task a merge request belongs to is the tracker's answer, not the document's.
                if (taskOfMr.GetValueOrDefault(mrId) != nodeTaskId)
                    errors.Add(
                        $"Merge request '{item.MergeRequestKey}' is listed under '{node.TaskKey}', but it "
                        + "belongs to another task. A document cannot move work between tasks.");
                else
                    waveOf[mrId] = item.Wave;
            }
        }

        foreach (var taskId in computed.Closure.Where(id => !seenTasks.Contains(id) && keys.TaskKeys.ContainsKey(id)))
            errors.Add($"Task '{keys.TaskKeys[taskId]}' is in this plan but missing from the document.");

        foreach (var mrId in computed.PlanMrs.Select(m => m.Id).Where(id => !waveOf.ContainsKey(id)))
            errors.Add(
                $"Merge request '{keys.MrKeys.GetValueOrDefault(mrId, mrId.ToString("D"))}' is in this plan "
                + "but missing from the document.");

        if (errors.Count > 0) return waveOf;

        // Contiguous from 1, because a wave is a position in a sequence and not a label. A document
        // jumping 1, 2, 4 leaves an empty stage: nothing deploys in wave 3, so what it asked for and
        // what would run are different plans.
        var waves = waveOf.Values.Distinct().OrderBy(w => w).ToList();
        for (var i = 0; i < waves.Count; i++)
            if (waves[i] != i + 1)
            {
                errors.Add(
                    $"Waves must run from 1 with no gaps; this document has {string.Join(", ", waves)}. "
                    + $"Wave {i + 1} is empty.");
                break;
            }

        return waveOf;
    }

    private static string Describe(IEnumerable<string> keys) =>
        string.Join(", ", keys.OrderBy(k => k, StringComparer.Ordinal));

    /// <summary>The 1-based wave each merge request landed in.</summary>
    private static Dictionary<Guid, int> WavesOf(PlanGraphResult graph)
    {
        var waveOf = new Dictionary<Guid, int>();
        for (var i = 0; i < graph.Stages.Count; i++)
            foreach (var id in graph.Stages[i]) waveOf[id] = i + 1;
        return waveOf;
    }

    /// <summary>
    /// Fingerprints what the plan says, so a rebuild that changed nothing can be told from one that did.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Hashes the ORDERED STAGES - the actual deploy order - plus the closure and the dropped
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
        IReadOnlyList<PlanEdge> RuleEdges,
        IReadOnlySet<Guid> ManuallyIncluded,
        PlanGraphResult Graph);

    /// <summary>Computes the target's closure and its ordered plan from the atlas -- shared by build and read.</summary>
    private async Task<Computed> ComputeAsync(Guid taskId, CancellationToken ct)
    {
        var adjacency = await LoadAdjacencyAsync(ct);
        var closure = PlanClosureBuilder.Closure(taskId, adjacency).ToHashSet();
        var closureIds = closure.Select(id => (Guid?)id).ToList();

        // Replayed on every build, exactly like the ordering deltas: membership is an operator
        // decision about the ROLLOUT, not a fact about the merge request, so it must survive the next
        // ingestion event rather than being re-derived away.
        var membership = await LoadMembershipOverridesAsync(taskId, ct);

        var planMrs = await db.MergeRequests
            // A closed (abandoned) merge request is not part of the rollout; everything else the
            // task owns is, so the tree shows the full picture. The executor decides at launch what
            // is already deployed and skips it.
            //
            // Either half is overridable. Excluding is the common one -- work that belongs to the
            // task but is deliberately not being shipped with it. Including brings back a merge
            // request the derivation dropped, which today means a closed one that is being deployed
            // anyway; without it the only recourse was reopening it in the provider.
            .Where(mr => closureIds.Contains(mr.TaskId)
                         && (mr.Status != MergeRequestStatus.Closed || membership.Include.Contains(mr.Id))
                         && !membership.Exclude.Contains(mr.Id))
            .OrderBy(mr => mr.CreatedAt).ThenBy(mr => mr.Id)
            .Select(PlanInput.FromEntity)
            .AsSplitQuery()
            .AsNoTracking()
            .ToListAsync(ct);

        // The SAME adjacency the closure came from decides the edges between merge requests. Read
        // straight from the tracker's navigations instead -- as this did -- and the wait policy holds
        // for which tasks the plan covers and not for the order they deploy in: group ordering and a
        // hand-set prerequisite order changed nothing at all, silently.
        var ordered = planMrs
            .Select(mr => mr.TaskId is { } tid && adjacency.TryGetValue(tid, out var prerequisites)
                ? mr.WithPrerequisites(prerequisites)
                : mr.WithPrerequisites([]))
            .ToList();

        // Replayed here, on every recalculation: this is what makes a hand-ordered plan survive the
        // next ingestion event instead of being silently rebuilt without the operator's edits.
        var (add, remove) = await LoadOverridesAsync(taskId, ct);

        var settings = await LoadSettingsAsync(ct);
        var ruleEdges = await LoadRuleEdgesAsync(settings.Rules, [.. ordered.Select(mr => mr.Id)], ct);

        return new Computed(
            closure, adjacency, ordered, ruleEdges, membership.Include,
            ReleasePlanGraph.Build(ordered, add, remove, ruleEdges));
    }

    /// <summary>
    /// The merge requests an operator forced into or out of this task's rollout.
    /// </summary>
    /// <remarks>
    /// Kept as deltas on the task and replayed on every build, for the same reason the ordering edits
    /// are: a plan is rebuilt on every ingestion event, so a decision recorded on the plan itself
    /// would last until the next webhook.
    ///
    /// Exclusion wins when a merge request is somehow named by both. That is the conservative
    /// direction - "do not deploy this" is a stronger statement than "deploy this", and the surprise
    /// of something not shipping is recoverable in a way that shipping it is not.
    /// </remarks>
    private async Task<(HashSet<Guid> Include, HashSet<Guid> Exclude)> LoadMembershipOverridesAsync(
        Guid taskId, CancellationToken ct)
    {
        var rows = await db.PlanOverrides
            .Where(o => o.TaskId == taskId
                        && (o.Kind == PlanOverrideKind.IncludeMr || o.Kind == PlanOverrideKind.ExcludeMr))
            .Select(o => new { o.Kind, o.Payload })
            .AsNoTracking()
            .ToListAsync(ct);

        HashSet<Guid> include = [];
        HashSet<Guid> exclude = [];

        foreach (var row in rows)
        {
            if (DeserializeMergeRequest(row.Payload) is not { } id) continue;

            if (row.Kind == PlanOverrideKind.IncludeMr) include.Add(id);
            else exclude.Add(id);
        }

        include.ExceptWith(exclude);
        return (include, exclude);
    }

    /// <summary>Reads a membership delta's payload, answering null for one that cannot be read.</summary>
    /// <remarks>Null rather than throwing, for the same reason as an unreadable edge: a bad row must
    /// not make the task unplannable, and the safe direction is the derived membership.</remarks>
    private static Guid? DeserializeMergeRequest(string payload)
    {
        try
        {
            var value = JsonSerializer.Deserialize<PlanMembershipOverridePayload>(payload);
            return value is null || value.MergeRequestId == Guid.Empty ? null : value.MergeRequestId;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The stored shape of an <c>IncludeMr</c>/<c>ExcludeMr</c> delta.</summary>
    /// <param name="MergeRequestId">The merge request forced into or out of the rollout.</param>
    private sealed record PlanMembershipOverridePayload(Guid MergeRequestId);

    /// <summary>Loads the whole task graph as adjacency, with the wait policy applied.</summary>
    /// <remarks>
    /// Two sources, one relation. Declared dependencies name who waits directly; the hierarchy says a
    /// parent waits on its children. Whether each counts is now a decision rather than an assumption
    /// - see <see cref="TaskWaitPolicy"/> - and the merge itself moved to the pure
    /// <see cref="TaskWaitGraph"/>, so it is testable without a database. What is left here is the
    /// loading.
    ///
    /// This adjacency decides the closure - which tasks a rollout even includes - so a source omitted
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

        var settings = await LoadSettingsAsync(ct);
        var overrideById = overrides.ToDictionary(o => o.Id);

        // The document layers over the stored default, then the task's own answers over that. A task
        // that disagreed explicitly outranks a blanket rule, which is the order an operator expects
        // when they set something on one task to escape the general policy.
        var global = OrderingRuleCompiler.ResolvePolicy(settings.Rules, settings.Policy, task: null);

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
    /// The installation-wide wait policy and ordering-rule document, or the built-in defaults when
    /// nobody has saved either.
    /// </summary>
    /// <remarks>
    /// Absent settings mean the defaults, not a failure: a fresh installation has no row, and it must
    /// plan exactly as it did before any of this existed rather than refuse to plan at all.
    ///
    /// An UNREADABLE document is treated the same way, and that is the uncomfortable call. Refusing to
    /// plan would be defensible, but it would take out every plan in the installation over one bad
    /// edit, including the plans of people who did not make it - so the rules are dropped, the derived
    /// ordering stands, and the problem is reported where it can be acted on (validate, and the log).
    /// The endpoint refuses to SAVE an invalid document, which is the gate that matters.
    /// </remarks>
    private async Task<(TaskWaitPolicy Policy, OrderingRules Rules)> LoadSettingsAsync(CancellationToken ct)
    {
        var settings = await db.PlanningSettings.AsNoTracking().FirstOrDefaultAsync(ct);

        if (settings is null) return (TaskWaitPolicy.Default, OrderingRules.Empty);

        var policy = new TaskWaitPolicy(
            settings.WaitForSubtasks, settings.WaitForLinked, settings.PrerequisiteGroupOrder);

        var parsed = OrderingRuleDocument.Read(settings.OrderingRulesDocument);
        if (!parsed.IsValid)
        {
            logger.LogError(
                "The stored ordering-rule document is invalid and is being ignored; plans use the derived "
                + "ordering only. Fix it via the planning rules endpoint. Problems: {Problems}",
                string.Join("; ", parsed.Errors));

            return (policy, OrderingRules.Empty);
        }

        return (policy, parsed.Rules!);
    }

    /// <summary>
    /// The ordering edges the rule document produces for one plan's merge requests.
    /// </summary>
    /// <remarks>
    /// Selectors are matched against the plan's own work, not against everything the installation
    /// holds: a rule orders what is being deployed, and reaching outside the plan would invent edges
    /// to merge requests the graph was never given (which it would then ignore anyway).
    /// </remarks>
    private async Task<List<PlanEdge>> LoadRuleEdgesAsync(
        OrderingRules rules, IReadOnlyList<Guid> mergeRequestIds, CancellationToken ct)
    {
        if (rules.Order.Count == 0 || mergeRequestIds.Count == 0) return [];

        var candidates = await db.MergeRequests
            .Where(m => mergeRequestIds.Contains(m.Id))
            .Select(m => new
            {
                m.Id, m.TaskId, m.SourceBranch, m.Labels,
                ConnectionName = m.Repository.Connection.Name,
                RepositoryExternalId = m.Repository.ExternalId,
                TaskKey = m.TaskExternalId
            })
            .AsNoTracking()
            .ToListAsync(ct);

        var compiled = OrderingRuleCompiler.Compile(
            rules,
            [.. candidates.Select(c => new OrderingCandidate(
                c.Id, c.TaskId, c.ConnectionName, c.RepositoryExternalId, c.SourceBranch, c.TaskKey,
                c.Labels.Split(',', StringSplitOptions.RemoveEmptyEntries)))]);

        // All or nothing. A partial cross product is an ordering nobody wrote -- it would constrain
        // some pairs and not others, arbitrarily, and read as deliberate. The derived ordering stands
        // instead, and the message names the rule to fix.
        if (compiled.LimitExceeded)
        {
            logger.LogError(
                "The ordering rules produce more than {Max} edges (reached on group '{Group}'), so they are "
                + "being ignored and plans use the derived ordering only. 'needs' is a cross product; narrow "
                + "the groups or scope the rule to within_task.",
                OrderingRuleCompiler.MaxEdges, compiled.ExceededOn);

            return [];
        }

        // Hard and soft map onto the kinds the graph already ranks when it has to break a cycle, so a
        // rule yields exactly as the repository ordering it generalises would.
        return [.. compiled.Edges.Select(e => new PlanEdge(
            e.From, e.To,
            e.Type == StackDependencyType.Hard ? PlanEdgeKind.RepoHard : PlanEdgeKind.RepoSoft))];
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
    /// - the alternative is an operator whose plan screen 500s with no way to clear the bad row.
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

    /// <inheritdoc/>
    /// <remarks>
    /// Renders the STORED plan, so an exported document and the rollout that runs describe the same
    /// order. That is also what makes the document importable: a schema that reports a fresh
    /// computation could never round-trip, since the thing it described was never stored anywhere.
    /// </remarks>
    public async Task<string?> ExportPlanYamlAsync(Guid taskId, CancellationToken ct = default)
    {
        var plan = await LoadStoredPlanAsync(taskId, ct);
        if (plan is null) return null;

        var (taskKeys, mrKeys) = await LoadKeysAsync(plan, ct);
        return RenderPlanYaml(plan, taskKeys, mrKeys);
    }

    /// <summary>The active plan for a task with its tree loaded, or null when it has none.</summary>
    private Task<RolloutPlan?> LoadStoredPlanAsync(Guid taskId, CancellationToken ct) =>
        db.RolloutPlans
            .Include(p => p.Nodes).ThenInclude(n => n.Items)
            .AsSplitQuery()
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.TargetTaskId == taskId && p.IsActive, ct);

    /// <summary>
    /// The human-readable keys a plan document is written in: task keys and merge-request keys.
    /// </summary>
    /// <remarks>
    /// The merge-request key is 006 §6's natural key - connection, repository path and the provider's
    /// own id. GitLab's vocabulary, but a key rather than a dialect leaking into the domain, and it is
    /// what lets a human read an exported plan and find the merge requests it names - and what lets
    /// import resolve them back without asking for database ids.
    /// </remarks>
    private async Task<(Dictionary<Guid, string> TaskKeys, Dictionary<Guid, string> MrKeys)> LoadKeysAsync(
        RolloutPlan plan, CancellationToken ct)
    {
        var closure = plan.Nodes.Select(n => n.TaskId).ToHashSet();
        var mrIds = plan.Nodes.SelectMany(n => n.Items).Select(i => i.MergeRequestId).ToList();

        var taskKeys = (await db.Tasks
                .Where(t => closure.Contains(t.Id))
                .Select(t => new { t.Id, t.ExternalId })
                .AsNoTracking()
                .ToListAsync(ct))
            .ToDictionary(t => t.Id, t => t.ExternalId);

        var mrKeys = (await db.MergeRequests
                .Where(m => mrIds.Contains(m.Id))
                .Select(m => new
                {
                    m.Id,
                    Key = m.Repository.Connection.Name + ":" + m.Repository.ExternalId + "!" + m.ExternalId
                })
                .AsNoTracking()
                .ToListAsync(ct))
            .ToDictionary(m => m.Id, m => m.Key);

        return (taskKeys, mrKeys);
    }

    /// <summary>
    /// Writes a plan out in the per-task schema.
    /// </summary>
    /// <remarks>
    /// Built as plain nested collections and handed to the YAML serializer rather than assembled by
    /// string concatenation: task keys and branch names are operator-supplied text, and deciding when
    /// one needs quoting is exactly the job a serializer exists to do correctly.
    ///
    /// <c>wave</c> is included and <c>order</c> is not. 006 §6 names an optional authored intra-task
    /// order; what this document reports is the wave the planner COMPUTED - but it is also the one
    /// field import reads back, because a wave assignment is the only thing about a plan an operator
    /// can state that the atlas does not already decide.
    /// </remarks>
    private static string RenderPlanYaml(
        RolloutPlan plan,
        IReadOnlyDictionary<Guid, string> taskKeys,
        IReadOnlyDictionary<Guid, string> mrKeys)
    {
        string TaskKey(Guid id) => taskKeys.GetValueOrDefault(id, id.ToString("D"));

        var nodes = plan.Nodes
            .Where(n => taskKeys.ContainsKey(n.TaskId))
            .OrderByDescending(n => n.TaskId == plan.TargetTaskId)
            .ThenBy(n => TaskKey(n.TaskId), StringComparer.Ordinal)
            .Select(n =>
            {
                var node = new Dictionary<string, object?> { ["task"] = TaskKey(n.TaskId) };

                var dependsOn = ReadDependsOn(n)
                    .Where(taskKeys.ContainsKey)
                    .Select(TaskKey)
                    .OrderBy(k => k, StringComparer.Ordinal)
                    .ToList();
                if (dependsOn.Count > 0) node["depends_on"] = dependsOn;

                var items = n.Items
                    .Where(i => mrKeys.ContainsKey(i.MergeRequestId))
                    .Select(i => new Dictionary<string, object?>
                    {
                        ["mr"] = mrKeys[i.MergeRequestId],
                        ["wave"] = i.Wave
                    })
                    .OrderBy(m => (int)m["wave"]!)
                    .ThenBy(m => (string)m["mr"]!, StringComparer.Ordinal)
                    .ToList();
                if (items.Count > 0) node["merge_requests"] = items;

                return node;
            })
            .ToList();

        var document = new Dictionary<string, object?>
        {
            ["version"] = 1,
            ["target_task"] = TaskKey(plan.TargetTaskId),
            ["plan_version"] = plan.Version,
            ["nodes"] = nodes
        };

        // Echoed read-only, exactly as 006 says: a plan may violate a constraint, but the document
        // that describes it must never read as clean while it does. Import ignores the key rather than
        // rejecting it, so a document can be exported, edited and fed straight back.
        var conflicts = ReadConflicts(plan);
        if (conflicts.Count > 0)
            document["conflicts"] = conflicts
                .Select(c => new Dictionary<string, object?>
                {
                    ["dropped"] = c.DroppedEdgeKind.ToString(),
                    ["from"] = mrKeys.GetValueOrDefault(c.FromMrId, c.FromMrId.ToString("D")),
                    ["to"] = mrKeys.GetValueOrDefault(c.ToMrId, c.ToMrId.ToString("D")),
                    ["reason"] = c.Reason
                })
                .ToList();

        var yaml = new SerializerBuilder().WithIndentedSequences().Build().Serialize(document);

        return "# Exported plan. Editable: change the 'wave' values and POST it back to\n"
             + "# /plan/import. Membership is not editable here -- which tasks and merge requests a\n"
             + "# plan covers comes from the atlas, and import rejects a document that disagrees.\n"
             + yaml;
    }

    /// <summary>
    /// Presents a stored plan: its tree, its waves and the constraints it could not honour, all read
    /// back from the row rather than derived again.
    /// </summary>
    /// <remarks>
    /// Only the display text - task titles, repository names, current merge-request status - is
    /// resolved live, because a name is not part of what the plan decided. The ORDER is not resolved
    /// live, and that is the whole distinction: renaming a repository must change the screen, whereas
    /// a new dependency link must not, until somebody recalculates.
    /// </remarks>
    private async Task<RolloutPlanDto> BuildStoredDtoAsync(RolloutPlan plan, CancellationToken ct)
    {
        var closure = plan.Nodes.Select(n => n.TaskId).ToHashSet();
        var taskById = (await db.Tasks
                .Where(t => closure.Contains(t.Id))
                .Select(t => new { t.Id, t.ExternalId, t.Title })
                .AsNoTracking()
                .ToListAsync(ct))
            .ToDictionary(t => t.Id);

        var mrIds = plan.Nodes.SelectMany(n => n.Items).Select(i => i.MergeRequestId).ToList();
        var mrById = (await db.MergeRequests
                .Where(m => mrIds.Contains(m.Id))
                .Select(m => new
                {
                    m.Id, m.ExternalId, m.SourceBranch, m.TargetBranch, m.CreatedAt,
                    RepoName = m.Repository.Name, m.Status
                })
                .AsNoTracking()
                .ToListAsync(ct))
            .ToDictionary(m => m.Id);

        var nodes = plan.Nodes
            .Where(n => taskById.ContainsKey(n.TaskId))
            .Select(n =>
            {
                var info = taskById[n.TaskId];
                var items = n.Items
                    .Where(i => mrById.ContainsKey(i.MergeRequestId))
                    .Select(i => (Item: i, Mr: mrById[i.MergeRequestId]))
                    .OrderBy(x => x.Mr.RepoName).ThenBy(x => x.Mr.ExternalId)
                    .Select(x => new PlanItemDto(
                        x.Mr.Id, x.Mr.ExternalId, x.Mr.RepoName, x.Mr.SourceBranch, x.Mr.TargetBranch,
                        x.Mr.Status.ToString(), x.Item.Wave, x.Item.ManualInclusion))
                    .ToList();

                return new PlanTaskNodeDto(
                    n.TaskId, info.ExternalId, info.Title, n.TaskId == plan.TargetTaskId,
                    ReadDependsOn(n).Where(closure.Contains).ToList(), items);
            })
            .OrderByDescending(n => n.IsTarget).ThenBy(n => n.TaskKey)
            .ToList();

        // Ties inside a wave keep the planner's own ordering rule -- oldest merge request first --
        // so the stored plan reads in the same sequence the derivation produced it in.
        var waves = plan.Nodes
            .SelectMany(n => n.Items)
            .Where(i => mrById.ContainsKey(i.MergeRequestId))
            .GroupBy(i => i.Wave)
            .OrderBy(g => g.Key)
            .Select(g => new PlanWaveDto(
                g.Key,
                [.. g.OrderBy(i => mrById[i.MergeRequestId].CreatedAt).ThenBy(i => i.MergeRequestId)
                     .Select(i => i.MergeRequestId)]))
            .ToList();

        var conflicts = ReadConflicts(plan)
            .Select(x => new PlanConflictDto(x.DroppedEdgeKind.ToString(), x.FromMrId, x.ToMrId, x.Reason))
            .ToList();

        var targetKey = taskById.TryGetValue(plan.TargetTaskId, out var tt) ? tt.ExternalId : string.Empty;

        return new RolloutPlanDto(
            plan.Id, plan.TargetTaskId, targetKey,
            plan.Version, plan.Source.ToString(), plan.Status.ToString(), plan.IsActive,
            plan.CreatedAt, plan.UpdatedAt, nodes, waves, conflicts);
    }

    /// <summary>The wait edges recorded on a node, empty when the plan predates the column or is unreadable.</summary>
    /// <remarks>
    /// Empty rather than throwing, for the same reason a bad override is skipped: a plan an operator
    /// cannot open is a plan they cannot fix. A missing tree edge understates the dependencies on
    /// screen, which the next recalculation restores.
    /// </remarks>
    private static IReadOnlyList<Guid> ReadDependsOn(PlanTaskNode node)
    {
        if (string.IsNullOrWhiteSpace(node.DependsOnTaskIdsJson)) return [];

        try
        {
            return JsonSerializer.Deserialize<List<Guid>>(node.DependsOnTaskIdsJson) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>The conflicts recorded on a plan, empty when there are none or the payload is unreadable.</summary>
    private static IReadOnlyList<PlanConflict> ReadConflicts(RolloutPlan plan)
    {
        if (string.IsNullOrWhiteSpace(plan.ConflictsJson)) return [];

        try
        {
            return JsonSerializer.Deserialize<List<PlanConflict>>(plan.ConflictsJson) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }
}
