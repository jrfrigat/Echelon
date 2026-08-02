using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ReleaseOrchestrator.Application.ReleasePlanning;
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

    /// <summary>The ordering-rule document as the operator wrote it.</summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The stored text, empty when none was ever saved.</returns>
    /// <remarks>
    /// Returns the author's own text rather than a re-serialised model, so comments and key order
    /// survive a round trip and the operator can diff it against their copy.
    /// </remarks>
    [HttpGet("rules")]
    public async Task<IActionResult> GetRules(CancellationToken ct)
    {
        var document = await db.PlanningSettings
            .AsNoTracking()
            .Select(s => s.OrderingRulesDocument)
            .FirstOrDefaultAsync(ct);

        return Ok(new OrderingRulesDocumentDto(document ?? ""));
    }

    /// <summary>
    /// Checks a document without saving it: syntax, internal consistency, and whether its selectors
    /// match anything currently configured.
    /// </summary>
    /// <param name="req">The document to check.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Whether it is valid, the problems found, and what each group currently selects.</returns>
    /// <remarks>
    /// The group counts are the useful half. A document can be perfectly valid and select nothing —
    /// a glob with a typo in it is still well-formed — and that is invisible until a deploy comes out
    /// in the wrong order. Reporting what each group matched right now turns that into something an
    /// operator sees before saving.
    /// </remarks>
    [HttpPost("rules/validate")]
    public async Task<IActionResult> ValidateRules([FromBody] OrderingRulesDocumentDto req, CancellationToken ct)
    {
        var parsed = OrderingRuleDocument.Read(req.Document);

        if (!parsed.IsValid)
            return Ok(new OrderingRulesValidationDto(false, parsed.Errors, []));

        var candidates = await LoadCandidatesAsync(ct);

        var groups = parsed.Rules!.Groups
            .Select(g => new OrderingRuleGroupMatchDto(
                g.Key,
                candidates.Count(g.Value.Matches),
                [.. candidates.Where(g.Value.Matches).Take(5).Select(c => $"{c.RepositoryExternalId}:{c.Branch}")]))
            .OrderBy(g => g.Group, StringComparer.Ordinal)
            .ToList();

        return Ok(new OrderingRulesValidationDto(true, [], groups));
    }

    /// <summary>Saves the ordering-rule document. An invalid one is refused.</summary>
    /// <param name="req">The document.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <remarks>
    /// This is the gate: the planner ignores an unreadable document rather than failing every plan in
    /// the installation, so refusing to store one is what keeps that from ever being reached.
    /// </remarks>
    [HttpPut("rules")]
    [Authorize(Policy = Permissions.ConfigEdit)]
    public async Task<IActionResult> SetRules([FromBody] OrderingRulesDocumentDto req, CancellationToken ct)
    {
        var parsed = OrderingRuleDocument.Read(req.Document);
        if (!parsed.IsValid)
            return BadRequest(new { error = "The ordering rules are not valid.", problems = parsed.Errors });

        var settings = await db.PlanningSettings.FirstOrDefaultAsync(ct);
        if (settings is null)
        {
            settings = new PlanningSettings { Id = PlanningSettings.SingletonId };
            db.PlanningSettings.Add(settings);
        }

        settings.OrderingRulesDocument = string.IsNullOrWhiteSpace(req.Document) ? null : req.Document;
        await db.SaveChangesAsync(ct);

        // Not recalculated here, as with the policy above: plans rebuild on the next ingestion event,
        // and an operator who wants it now presses Recalculate.
        return NoContent();
    }

    /// <summary>Every merge request the rules could select, as the selectors read them.</summary>
    private async Task<List<OrderingCandidate>> LoadCandidatesAsync(CancellationToken ct)
    {
        var rows = await db.MergeRequests
            .Where(m => m.Status != MergeRequestStatus.Closed)
            .Select(m => new
            {
                m.Id, m.TaskId, m.SourceBranch, m.Labels,
                ConnectionName = m.Repository.Connection.Name,
                RepositoryExternalId = m.Repository.ExternalId,
                TaskKey = m.TaskExternalId
            })
            .AsNoTracking()
            .ToListAsync(ct);

        return [.. rows.Select(r => new OrderingCandidate(
            r.Id, r.TaskId, r.ConnectionName, r.RepositoryExternalId, r.SourceBranch, r.TaskKey,
            r.Labels.Split(',', StringSplitOptions.RemoveEmptyEntries)))];
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

/// <summary>The ordering-rule document, as text.</summary>
/// <param name="Document">
/// The rules. JSON today, YAML once the parser can be installed — the same keys and nesting either
/// way, since YAML 1.2 is a superset of JSON. Empty means no rules.
/// </param>
public record OrderingRulesDocumentDto(string Document);

/// <summary>What checking a document found.</summary>
/// <param name="IsValid">Whether it would be accepted.</param>
/// <param name="Problems">Everything wrong with it; empty when valid.</param>
/// <param name="Groups">What each group selects right now — a valid rule can still match nothing.</param>
public record OrderingRulesValidationDto(
    bool IsValid, IReadOnlyList<string> Problems, IReadOnlyList<OrderingRuleGroupMatchDto> Groups);

/// <summary>What one group currently selects.</summary>
/// <param name="Group">The group's name.</param>
/// <param name="Matched">How many merge requests it selects.</param>
/// <param name="Examples">A few of them, as <c>repository:branch</c>, to confirm it selected what was meant.</param>
public record OrderingRuleGroupMatchDto(string Group, int Matched, IReadOnlyList<string> Examples);

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
