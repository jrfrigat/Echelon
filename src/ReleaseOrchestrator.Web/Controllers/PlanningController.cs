using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ReleaseOrchestrator.Core.Enums;
using ReleaseOrchestrator.Infrastructure.Auth;
using ReleaseOrchestrator.Infrastructure.Persistence;
using ReleaseOrchestrator.Infrastructure.Persistence.Models;

namespace ReleaseOrchestrator.Web.Controllers;

/// <summary>
/// What a rollout waits for: the installation-wide defaults, and a task's departures from them.
/// </summary>
/// <remarks>
/// These are planner INPUTS, never part of a generated plan. Every ingestion event rebuilds every
/// active plan, so anything stored on the plan itself would be replaced by the next event; stored
/// here, a decision survives by construction and is replayed on each rebuild.
/// </remarks>
[ApiController]
[Route("api/planning")]
[Authorize(Policy = Permissions.ReleasePlanView)]
public class PlanningController(AppDbContext db) : ControllerBase
{
    /// <summary>The installation-wide wait policy.</summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The saved settings, or the built-in defaults when none were ever saved.</returns>
    [HttpGet("policy")]
    public async Task<IActionResult> GetPolicy(CancellationToken ct)
    {
        var settings = await db.PlanningSettings.AsNoTracking().FirstOrDefaultAsync(ct);

        // Defaults rather than 404: a fresh installation has no row and plans by the defaults, so
        // that is the honest answer to "what is in force".
        return Ok(new PlanningPolicyDto(
            settings?.WaitForSubtasks ?? true,
            settings?.WaitForLinked ?? true,
            (settings?.PrerequisiteGroupOrder ?? PrerequisiteGroupOrder.Together).ToString()));
    }

    /// <summary>Saves the installation-wide wait policy.</summary>
    /// <param name="req">The policy to store.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpPut("policy")]
    [Authorize(Policy = Permissions.ConfigEdit)]
    public async Task<IActionResult> SetPolicy([FromBody] PlanningPolicyDto req, CancellationToken ct)
    {
        if (!TryParseGroupOrder(req.PrerequisiteGroupOrder, out var order))
            return BadRequest(new { error = $"Unknown group order '{req.PrerequisiteGroupOrder}'." });

        var settings = await db.PlanningSettings.FirstOrDefaultAsync(ct);
        if (settings is null)
        {
            settings = new PlanningSettings { Id = PlanningSettings.SingletonId };
            db.PlanningSettings.Add(settings);
        }

        settings.WaitForSubtasks = req.WaitForSubtasks;
        settings.WaitForLinked = req.WaitForLinked;
        settings.PrerequisiteGroupOrder = order;
        await db.SaveChangesAsync(ct);

        // Not recalculated here: plans rebuild on the next ingestion event, and an operator who wants
        // it now presses Recalculate on the task. Rebuilding every active plan from a settings save
        // would be an unbounded amount of work behind a PUT.
        return NoContent();
    }

    /// <summary>A task's own departures from the installation defaults, and its explicit orderings.</summary>
    /// <param name="taskId">The task.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet("tasks/{taskId:guid}")]
    public async Task<IActionResult> GetTaskPolicy(Guid taskId, CancellationToken ct)
    {
        var task = await db.Tasks
            .Where(t => t.Id == taskId)
            .Select(t => new { t.WaitForSubtasks, t.WaitForLinked, t.PrerequisiteGroupOrder })
            .AsNoTracking()
            .FirstOrDefaultAsync(ct);

        if (task is null) return NotFound();

        var prerequisiteOrder = await db.TaskPrerequisiteOrders
            .AsNoTracking()
            .Where(o => o.TaskId == taskId)
            .OrderBy(o => o.Position)
            .Select(o => o.PrerequisiteTaskId)
            .ToListAsync(ct);

        return Ok(new TaskPlanningDto(
            task.WaitForSubtasks,
            task.WaitForLinked,
            task.PrerequisiteGroupOrder?.ToString(),
            prerequisiteOrder,
            await ReadMergeRequestOrderAsync(taskId, ct)));
    }

    /// <summary>
    /// Saves a task's departures from the defaults. A null field means "inherit".
    /// </summary>
    /// <param name="taskId">The task.</param>
    /// <param name="req">The overrides and orderings.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <remarks>
    /// Null and false are different answers and both are stored: null follows the installation
    /// default wherever it moves, false stays false when the default changes. Collapsing them would
    /// make "I do not care" indistinguishable from "definitely not".
    /// </remarks>
    [HttpPut("tasks/{taskId:guid}")]
    [Authorize(Policy = Permissions.ReleasePlanApprove)]
    public async Task<IActionResult> SetTaskPolicy(Guid taskId, [FromBody] TaskPlanningDto req, CancellationToken ct)
    {
        var task = await db.Tasks.FirstOrDefaultAsync(t => t.Id == taskId, ct);
        if (task is null) return NotFound();

        PrerequisiteGroupOrder? order = null;
        if (req.PrerequisiteGroupOrder is { Length: > 0 })
        {
            if (!TryParseGroupOrder(req.PrerequisiteGroupOrder, out var parsed))
                return BadRequest(new { error = $"Unknown group order '{req.PrerequisiteGroupOrder}'." });
            order = parsed;
        }

        task.WaitForSubtasks = req.WaitForSubtasks;
        task.WaitForLinked = req.WaitForLinked;
        task.PrerequisiteGroupOrder = order;

        await ReplacePrerequisiteOrderAsync(taskId, req.PrerequisiteOrder ?? [], ct);
        await ReplaceMergeRequestOrderAsync(taskId, req.MergeRequestOrder ?? [], ct);

        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    /// <summary>Replaces the explicit sequence over a task's prerequisites.</summary>
    /// <remarks>
    /// Replaced wholesale rather than diffed: the sequence is positional, and a partial update would
    /// have to renumber around the rows it kept — against a unique index on (task, position), which
    /// would collide mid-update. Deleting first keeps it a single coherent write.
    /// </remarks>
    private async Task ReplacePrerequisiteOrderAsync(Guid taskId, IReadOnlyList<Guid> sequence, CancellationToken ct)
    {
        await db.TaskPrerequisiteOrders.Where(o => o.TaskId == taskId).ExecuteDeleteAsync(ct);

        var position = 0;
        foreach (var prerequisiteId in sequence.Distinct().Where(id => id != taskId))
            db.TaskPrerequisiteOrders.Add(new TaskPrerequisiteOrder
            {
                Id = Guid.NewGuid(),
                TaskId = taskId,
                PrerequisiteTaskId = prerequisiteId,
                Position = position++
            });
    }

    /// <summary>
    /// Records a desired merge-request order as the ordering edges the planner replays.
    /// </summary>
    /// <remarks>
    /// The client sends a sequence, not edges: an operator reorders a list, and turning that into
    /// "A before B" pairs is this service's job, not the browser's. Chained consecutively — "after" is
    /// transitive through the graph, so the pairs are enough and a full cross product would only add
    /// edges that say the same thing.
    ///
    /// Note these are ADDED edges, so they constrain rather than replace: a derived constraint the
    /// sequence contradicts stays, the graph becomes cyclic, and the planner drops an edge and records
    /// the conflict. That is the intended outcome — an operator may reorder against the derivation,
    /// but never silently.
    /// </remarks>
    private async Task ReplaceMergeRequestOrderAsync(Guid taskId, IReadOnlyList<Guid> sequence, CancellationToken ct)
    {
        await db.PlanOverrides
            .Where(o => o.TaskId == taskId && o.Kind == PlanOverrideKind.AddEdge)
            .ExecuteDeleteAsync(ct);

        var ordered = sequence.Distinct().ToList();
        for (var i = 1; i < ordered.Count; i++)
            db.PlanOverrides.Add(new PlanOverride
            {
                Id = Guid.NewGuid(),
                TaskId = taskId,
                Kind = PlanOverrideKind.AddEdge,
                Payload = JsonSerializer.Serialize(new { From = ordered[i - 1], To = ordered[i] })
            });
    }

    /// <summary>Reads the stored edge deltas back as the sequence the operator arranged.</summary>
    private async Task<List<Guid>> ReadMergeRequestOrderAsync(Guid taskId, CancellationToken ct)
    {
        var payloads = await db.PlanOverrides
            .Where(o => o.TaskId == taskId && o.Kind == PlanOverrideKind.AddEdge)
            .Select(o => o.Payload)
            .AsNoTracking()
            .ToListAsync(ct);

        var edges = new List<(Guid From, Guid To)>();
        foreach (var payload in payloads)
        {
            try
            {
                var edge = JsonSerializer.Deserialize<EdgePayload>(payload);
                if (edge is not null && edge.From != Guid.Empty && edge.To != Guid.Empty)
                    edges.Add((edge.From, edge.To));
            }
            catch (JsonException)
            {
                // An unreadable delta is skipped rather than failing the read: the screen that would
                // let an operator clear it must still open.
            }
        }

        // Rebuild the chain: the head is the one nothing points at, then follow the links.
        var next = edges.ToDictionary(e => e.From, e => e.To);
        var head = edges.Select(e => e.From).Except(edges.Select(e => e.To)).FirstOrDefault();

        var sequence = new List<Guid>();
        var cursor = head;
        while (cursor != Guid.Empty && sequence.Count <= edges.Count)
        {
            sequence.Add(cursor);
            if (!next.TryGetValue(cursor, out var following)) break;
            cursor = following;
        }

        return sequence;
    }

    private static bool TryParseGroupOrder(string? value, out PrerequisiteGroupOrder order) =>
        Enum.TryParse(value, ignoreCase: true, out order) && Enum.IsDefined(order);

    private sealed record EdgePayload(Guid From, Guid To);
}

/// <summary>The installation-wide wait policy.</summary>
/// <param name="WaitForSubtasks">Whether a parent waits for the tasks beneath it.</param>
/// <param name="WaitForLinked">Whether a task waits for the tasks it declares a dependency on.</param>
/// <param name="PrerequisiteGroupOrder">
/// <c>Together</c>, <c>SubtasksFirst</c> or <c>LinkedFirst</c>. See the enum of the same name.
/// </param>
public record PlanningPolicyDto(bool WaitForSubtasks, bool WaitForLinked, string PrerequisiteGroupOrder);

/// <summary>One task's departures from the installation defaults, and its explicit orderings.</summary>
/// <param name="WaitForSubtasks">The task's answer, or null to inherit.</param>
/// <param name="WaitForLinked">The task's answer, or null to inherit.</param>
/// <param name="PrerequisiteGroupOrder">The task's answer, or null/empty to inherit.</param>
/// <param name="PrerequisiteOrder">The order to wait on prerequisite tasks in; empty for none.</param>
/// <param name="MergeRequestOrder">The order to deploy this task's merge requests in; empty for none.</param>
public record TaskPlanningDto(
    bool? WaitForSubtasks,
    bool? WaitForLinked,
    string? PrerequisiteGroupOrder,
    IReadOnlyList<Guid>? PrerequisiteOrder,
    IReadOnlyList<Guid>? MergeRequestOrder);
