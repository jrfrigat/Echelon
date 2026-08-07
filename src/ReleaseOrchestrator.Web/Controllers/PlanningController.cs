using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Rebus.Bus;
using ReleaseOrchestrator.Application.Contracts.Messages;
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
public class PlanningController(AppDbContext db, IBus bus, TimeProvider clock) : ControllerBase
{
    /// <summary>
    /// Asks for every active plan to be rebuilt, because something that decides deploy order changed.
    /// </summary>
    /// <remarks>
    /// Sent rather than done here: rebuilding every active plan inside a PUT is unbounded work behind
    /// a request. It was previously left to the next ingestion event, which was defensible only while
    /// the plan screen recomputed on read and therefore looked up to date. It no longer does — a plan
    /// version now means what it says — so a settings change nobody rebuilt is a change nobody sees.
    /// This is the same trigger repository ordering has always used, for the same reason.
    /// </remarks>
    private Task RequestRecalculationAsync(string reason) =>
        bus.Send(new ReleasePlanRecalculationRequested(clock.GetUtcNow().UtcDateTime, reason));

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
        await RequestRecalculationAsync("Installation wait policy changed");
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
    /// The repository-ordering rules that exist today, written out as a document.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A document equivalent to the current rules, ready to review and save.</returns>
    /// <remarks>
    /// Adopting the document as the source of truth turns the ordering screen read-only, so this
    /// exists to make that a copy-paste rather than a re-typing exercise. Deliberately does NOT save:
    /// the operator should read what they are about to make authoritative, and this is also the moment
    /// they usually notice a rule nobody remembers adding.
    /// </remarks>
    [HttpGet("rules/from-repository-ordering")]
    public async Task<IActionResult> RulesFromRepositoryOrdering(CancellationToken ct)
    {
        var rules = await db.RepositoryDependencies
            .Select(d => new
            {
                From = d.FromRepository.ExternalId,
                To = d.ToRepository.ExternalId,
                d.Type
            })
            .OrderBy(d => d.From).ThenBy(d => d.To)
            .AsNoTracking()
            .ToListAsync(ct);

        return Ok(new OrderingRulesDocumentDto(RenderDocument(rules.Select(r => (r.From, r.To, r.Type)))));
    }

    /// <summary>
    /// Writes repository pairs out as a document: one group per repository, one rule per pair.
    /// </summary>
    /// <remarks>
    /// A group per repository rather than anything cleverer. The point is a faithful translation the
    /// operator can read line by line against the screen they are replacing — inferring groups would
    /// produce a shorter document that is harder to check, at the moment checking matters most. It is
    /// theirs to simplify afterwards.
    /// </remarks>
    private static string RenderDocument(IEnumerable<(string From, string To, StackDependencyType Type)> pairs)
    {
        var rules = pairs.ToList();
        if (rules.Count == 0)
            return "version: 1\n\n# No repository-ordering rules are configured.\n";

        var repositories = rules.SelectMany(r => new[] { r.From, r.To })
            .Distinct(StringComparer.Ordinal)
            .OrderBy(r => r, StringComparer.Ordinal)
            .ToList();

        var names = repositories.ToDictionary(r => r, GroupName, StringComparer.Ordinal);

        var text = new StringBuilder()
            .AppendLine("version: 1")
            .AppendLine()
            .AppendLine("# Generated from the repository-ordering rules that were configured on screen.")
            .AppendLine("# Review it, then save to make the document the source of truth.")
            .AppendLine()
            .AppendLine("groups:");

        foreach (var repository in repositories)
            text.AppendLine($"  {names[repository]}:")
                .AppendLine($"    repositories: [\"{repository}\"]");

        text.AppendLine().AppendLine("order:");

        foreach (var rule in rules)
            text.AppendLine($"  - group: {names[rule.From]}")
                .AppendLine($"    needs: [{names[rule.To]}]")
                .AppendLine($"    type: {(rule.Type == StackDependencyType.Hard ? "hard" : "soft")}");

        return text.ToString();
    }

    /// <summary>A group name from a repository path, kept readable and unique enough to hand-edit.</summary>
    private static string GroupName(string repositoryExternalId)
    {
        var name = repositoryExternalId.Replace('/', '-').Replace('.', '-');
        return string.Concat(name.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_'));
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

        // Compiled against the real work, not just parsed: 'needs' is a cross product, and a document
        // that is perfectly well-formed can still expand into more edges than the planner will accept.
        // Caught here so it cannot be stored and discovered later from plans quietly losing their rules.
        var compiled = OrderingRuleCompiler.Compile(parsed.Rules, candidates);
        if (compiled.LimitExceeded)
            return Ok(new OrderingRulesValidationDto(
                false,
                [$"These rules expand to more than {OrderingRuleCompiler.MaxEdges} ordering edges, reached on "
                 + $"group '{compiled.ExceededOn}'. 'needs' orders every merge request of one group after every "
                 + "merge request of another, so a large pair multiplies. Narrow the groups, or add "
                 + "scope: within_task so the rule only orders within a single task."],
                groups));

        return Ok(new OrderingRulesValidationDto(true, [], groups));
    }

    /// <summary>
    /// The ordering rules as a structure, for editing them by clicking rather than typing.
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The parsed document, or an empty model when none is stored or it cannot be read.</returns>
    /// <remarks>
    /// Parsed on the server, deliberately. The client could not do it without shipping a YAML reader
    /// into the browser, and then two readers would decide what a document means — the exact
    /// divergence the single-document design exists to prevent. This one is the planner's.
    ///
    /// An unreadable document answers <c>Editable: false</c> rather than an error: the text editor
    /// above it is how you fix that, and a form silently offering to overwrite a document it failed
    /// to understand would lose whatever the author actually wrote.
    /// </remarks>
    [HttpGet("rules/model")]
    public async Task<IActionResult> GetRulesModel(CancellationToken ct)
    {
        var document = await db.PlanningSettings
            .AsNoTracking()
            .Select(s => s.OrderingRulesDocument)
            .FirstOrDefaultAsync(ct);

        if (string.IsNullOrWhiteSpace(document))
            return Ok(new OrderingRulesModelDto(true, [], [], []));

        var parsed = OrderingRuleDocument.Read(document);
        return parsed.IsValid
            ? Ok(ToModel(parsed.Rules!))
            : Ok(new OrderingRulesModelDto(false, parsed.Errors, [], []));
    }

    /// <summary>
    /// Renders a structure into a document without saving it.
    /// </summary>
    /// <param name="req">The model the editor built.</param>
    /// <remarks>
    /// A preview, so an operator can see the text their clicks produced before it becomes the
    /// installation's deploy order. It also proves the round trip: the rendered document is read back
    /// with the same reader the planner uses, and a model that cannot survive that is refused here
    /// rather than stored.
    /// </remarks>
    [HttpPost("rules/model/render")]
    public IActionResult RenderRulesModel([FromBody] OrderingRulesModelDto req)
    {
        var (document, errors) = Render(req);
        return errors.Count > 0
            ? BadRequest(new { error = "The rules could not be written.", problems = errors })
            : Ok(new OrderingRulesDocumentDto(document));
    }

    /// <summary>
    /// Renders a structure and stores it as the ordering-rule document.
    /// </summary>
    /// <param name="req">The model the editor built.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <remarks>
    /// Goes through exactly the same gate as saving the text: rendered, then read back, then stored
    /// only if valid. The editor gets no privileged path to the database.
    /// </remarks>
    [HttpPut("rules/model")]
    [Authorize(Policy = Permissions.ConfigEdit)]
    public async Task<IActionResult> SetRulesModel([FromBody] OrderingRulesModelDto req, CancellationToken ct)
    {
        var (document, errors) = Render(req);
        if (errors.Count > 0)
            return BadRequest(new { error = "The rules could not be written.", problems = errors });

        return await SetRules(new OrderingRulesDocumentDto(document), ct);
    }

    /// <summary>
    /// Writes a model out and reads it straight back, so a document the editor cannot round-trip
    /// never reaches storage.
    /// </summary>
    /// <returns>The rendered text, and everything wrong with it.</returns>
    private static (string Document, IReadOnlyList<string> Errors) Render(OrderingRulesModelDto req)
    {
        var rules = new OrderingRules(
            1,
            new TaskPolicySpec(null, null, null, []),
            req.Groups.ToDictionary(
                g => g.Name,
                g => new WorkSelector(
                    g.Connectors ?? [], g.Repositories ?? [], g.Branches ?? [],
                    g.TaskKeys ?? [], g.Labels ?? []),
                StringComparer.Ordinal),
            [.. req.Order.Select(o => new GroupOrderSpec(
                o.Group,
                o.Needs ?? [],
                string.Equals(o.Type, "Soft", StringComparison.OrdinalIgnoreCase)
                    ? StackDependencyType.Soft
                    : StackDependencyType.Hard,
                string.Equals(o.Scope, "WithinTask", StringComparison.OrdinalIgnoreCase)
                    ? OrderScope.WithinTask
                    : OrderScope.AcrossPlan))]);

        var document = OrderingRuleWriter.Write(rules);

        // The round trip is the check. Anything the form let through that the language does not
        // accept -- a blank group name, a 'needs' pointing at a group nobody defined -- surfaces here
        // as the reader's own message, which is the message the text editor would have given.
        var parsed = OrderingRuleDocument.Read(document);
        return (document, parsed.IsValid ? [] : parsed.Errors);
    }

    /// <summary>Projects the parsed rules onto the editor's shape.</summary>
    /// <remarks>
    /// The wait policy and the per-task overrides are carried in the document but not offered by the
    /// form: they are edited on their own screens, and duplicating them here would give an operator
    /// two places to set one thing. The renderer therefore writes them empty — which is why saving
    /// from the editor is refused while a document defines them (see the page).
    /// </remarks>
    private static OrderingRulesModelDto ToModel(OrderingRules rules) =>
        new(
            // Editable only when the form can express the whole document. A document using the task
            // policy or nested excludes would come back through the renderer with those silently
            // dropped, so the form declines to own it rather than quietly deleting a rule.
            Editable:
                rules.Tasks is { WaitForSubtasks: null, WaitForLinked: null, GroupOrder: null, Overrides.Count: 0 }
                && rules.Groups.Values.All(g => g.Exclude is null),
            Problems: [],
            Groups: [.. rules.Groups
                .OrderBy(g => g.Key, StringComparer.Ordinal)
                .Select(g => new OrderingRuleGroupDto(
                    g.Key, g.Value.Connectors, g.Value.Repositories, g.Value.Branches,
                    g.Value.TaskKeys, g.Value.Labels))],
            Order: [.. rules.Order.Select(o => new OrderingRuleOrderDto(
                o.Group, o.Needs, o.Type.ToString(), o.Scope.ToString()))]);

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
        await RequestRecalculationAsync("Ordering-rule document changed");
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
        await RequestRecalculationAsync($"Planning overrides changed for task {taskId}");
        return NoContent();
    }

    /// <summary>
    /// The merge requests an operator forced into or out of this task's rollout.
    /// </summary>
    /// <param name="taskId">The task.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <remarks>
    /// Resolved to display keys here rather than returned as bare ids. An excluded merge request is
    /// absent from the plan by construction, so this endpoint is the only place it can be seen — and
    /// a list of GUIDs would make the decision effectively irreversible.
    /// </remarks>
    [HttpGet("tasks/{taskId:guid}/membership")]
    public async Task<IActionResult> GetMembership(Guid taskId, CancellationToken ct)
    {
        var rows = await db.PlanOverrides
            .Where(o => o.TaskId == taskId
                        && (o.Kind == PlanOverrideKind.IncludeMr || o.Kind == PlanOverrideKind.ExcludeMr))
            .Select(o => new { o.Kind, o.Payload })
            .AsNoTracking()
            .ToListAsync(ct);

        var byId = new Dictionary<Guid, PlanOverrideKind>();
        foreach (var row in rows)
            if (ReadMembershipPayload(row.Payload) is { } id)
                byId[id] = row.Kind;

        var ids = byId.Keys.ToList();
        var details = await db.MergeRequests
            .Where(m => ids.Contains(m.Id))
            .Select(m => new
            {
                m.Id, m.ExternalId, m.SourceBranch, m.Status,
                RepositoryName = m.Repository.Name
            })
            .AsNoTracking()
            .ToListAsync(ct);

        return Ok(details
            .Select(m => new PlanMembershipDto(
                m.Id, m.ExternalId, m.RepositoryName, m.SourceBranch, m.Status.ToString(),
                byId[m.Id] == PlanOverrideKind.IncludeMr ? "Included" : "Excluded"))
            .OrderBy(m => m.RepositoryName, StringComparer.Ordinal)
            .ThenBy(m => m.MrExternalId, StringComparer.Ordinal)
            .ToList());
    }

    /// <summary>
    /// Forces one merge request into or out of a task's rollout, or hands it back to the derivation.
    /// </summary>
    /// <param name="taskId">The task.</param>
    /// <param name="mergeRequestId">The merge request.</param>
    /// <param name="req">The decision: <c>auto</c>, <c>included</c> or <c>excluded</c>.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <remarks>
    /// One merge request per call rather than a replace-the-set body. Two operators excluding
    /// different merge requests from the same plan would otherwise overwrite each other with a stale
    /// list, and the loser's exclusion would come back on the next rebuild without anyone noticing.
    /// </remarks>
    [HttpPut("tasks/{taskId:guid}/membership/{mergeRequestId:guid}")]
    [Authorize(Policy = Permissions.ReleasePlanApprove)]
    public async Task<IActionResult> SetMembership(
        Guid taskId, Guid mergeRequestId, [FromBody] PlanMembershipRequest req, CancellationToken ct)
    {
        if (!await db.Tasks.AnyAsync(t => t.Id == taskId, ct)) return NotFound();
        if (!await db.MergeRequests.AnyAsync(m => m.Id == mergeRequestId, ct))
            return NotFound(new { error = "Unknown merge request." });

        PlanOverrideKind? kind;
        switch (req.State?.ToLowerInvariant())
        {
            case null or "" or "auto": kind = null; break;
            case "included": kind = PlanOverrideKind.IncludeMr; break;
            case "excluded": kind = PlanOverrideKind.ExcludeMr; break;
            default:
                return BadRequest(new
                {
                    error = $"Unknown membership state '{req.State}'. Valid values: auto, included, excluded."
                });
        }

        // Both kinds are cleared first, so the three states are genuinely exclusive and re-applying
        // one is idempotent rather than accumulating rows that all replay.
        var payload = JsonSerializer.Serialize(new { MergeRequestId = mergeRequestId });
        var existing = await db.PlanOverrides
            .Where(o => o.TaskId == taskId
                        && (o.Kind == PlanOverrideKind.IncludeMr || o.Kind == PlanOverrideKind.ExcludeMr))
            .ToListAsync(ct);

        foreach (var row in existing.Where(r => ReadMembershipPayload(r.Payload) == mergeRequestId))
            db.PlanOverrides.Remove(row);

        if (kind is { } chosen)
            db.PlanOverrides.Add(new PlanOverride
            {
                Id = Guid.NewGuid(),
                TaskId = taskId,
                Kind = chosen,
                Payload = payload
            });

        await db.SaveChangesAsync(ct);
        await RequestRecalculationAsync($"Plan membership changed for task {taskId}");
        return NoContent();
    }

    /// <summary>Reads a membership delta's merge-request id, or null when the payload is unreadable.</summary>
    private static Guid? ReadMembershipPayload(string payload)
    {
        try
        {
            var value = JsonSerializer.Deserialize<MembershipPayload>(payload);
            return value is null || value.MergeRequestId == Guid.Empty ? null : value.MergeRequestId;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed record MembershipPayload(Guid MergeRequestId);

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

        return Linearise(edges);
    }

    /// <summary>Reads a set of "A before B" edges back as one sequence.</summary>
    /// <remarks>
    /// <para>
    /// A topological read, not a walk down a chain. What this screen WRITES is a chain — an operator
    /// drags a list into an order — but an imported plan writes a wave assignment, which pins several
    /// merge requests to the same predecessor. The chain walk could not represent that, and its
    /// <c>ToDictionary(e => e.From)</c> did worse than misread it: two edges out of one merge request
    /// threw, so opening the planning screen of any imported task answered 500.
    /// </para>
    /// <para>
    /// Reading a partial order as a sequence loses the parallelism, which is honest for a control that
    /// only offers a sequence. Saving from that control then flattens the import into a strict order —
    /// deliberately, because the operator just stated one — and the import can be posted again.
    /// </para>
    /// <para>
    /// Kahn's algorithm, keyed on nothing but the edges. A cycle (which the planner would have broken
    /// anyway) leaves nodes unplaced; they are appended rather than dropped, so the screen shows every
    /// merge request the deltas mention even when they contradict each other.
    /// </para>
    /// </remarks>
    private static List<Guid> Linearise(List<(Guid From, Guid To)> edges)
    {
        var nodes = edges.SelectMany(e => new[] { e.From, e.To }).Distinct().ToList();
        var successors = edges.ToLookup(e => e.From, e => e.To);
        var inDegree = nodes.ToDictionary(n => n, _ => 0);
        foreach (var edge in edges) inDegree[edge.To]++;

        var sequence = new List<Guid>();
        var ready = new Queue<Guid>(nodes.Where(n => inDegree[n] == 0));

        while (ready.Count > 0)
        {
            var node = ready.Dequeue();
            sequence.Add(node);

            foreach (var next in successors[node])
                if (--inDegree[next] == 0) ready.Enqueue(next);
        }

        sequence.AddRange(nodes.Where(n => !sequence.Contains(n)));
        return sequence;
    }

    private static bool TryParseGroupOrder(string? value, out PrerequisiteGroupOrder order) =>
        Enum.TryParse(value, ignoreCase: true, out order) && Enum.IsDefined(order);

    private sealed record EdgePayload(Guid From, Guid To);
}

/// <summary>The ordering-rule document, as text.</summary>
/// <param name="Document">
/// The rules, as YAML. A document written as JSON is also accepted, since JSON is valid YAML — which
/// is what keeps anything stored before the YAML reader existed readable. Empty means no rules.
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

/// <summary>A merge request an operator forced into or out of a task's rollout.</summary>
/// <param name="MergeRequestId">The merge request.</param>
/// <param name="MrExternalId">Its provider id, for display.</param>
/// <param name="RepositoryName">Its repository, for display.</param>
/// <param name="SourceBranch">Its branch, for display.</param>
/// <param name="MrStatus">Its current status — an excluded merge request may since have been merged.</param>
/// <param name="State">"Included" or "Excluded".</param>
public record PlanMembershipDto(
    Guid MergeRequestId,
    string MrExternalId,
    string RepositoryName,
    string SourceBranch,
    string MrStatus,
    string State);

/// <summary>A named selector, as the visual editor holds it.</summary>
/// <param name="Name">The group name that <c>order</c> entries refer to.</param>
/// <param name="Connectors">Connection-name globs.</param>
/// <param name="Repositories">Repository external-id globs.</param>
/// <param name="Branches">Source-branch globs.</param>
/// <param name="TaskKeys">Task-key globs.</param>
/// <param name="Labels">Labels, matched exactly.</param>
public record OrderingRuleGroupDto(
    string Name,
    IReadOnlyList<string>? Connectors,
    IReadOnlyList<string>? Repositories,
    IReadOnlyList<string>? Branches,
    IReadOnlyList<string>? TaskKeys,
    IReadOnlyList<string>? Labels);

/// <summary>One ordering rule, as the visual editor holds it.</summary>
/// <param name="Group">The group that waits.</param>
/// <param name="Needs">The groups it waits for.</param>
/// <param name="Type">"Hard" or "Soft".</param>
/// <param name="Scope">"AcrossPlan" or "WithinTask".</param>
public record OrderingRuleOrderDto(
    string Group,
    IReadOnlyList<string>? Needs,
    string? Type,
    string? Scope);

/// <summary>The ordering rules as a structure the visual editor can hold.</summary>
/// <param name="Editable">
/// False when the stored document says something the form cannot express — a wait policy, a per-task
/// override, a nested exclude. Saving from the form would drop it, so the form refuses to own it and
/// the text editor stays the way in.
/// </param>
/// <param name="Problems">Why the stored document could not be read, when it could not.</param>
/// <param name="Groups">The named selectors.</param>
/// <param name="Order">The ordering between them.</param>
public record OrderingRulesModelDto(
    bool Editable,
    IReadOnlyList<string> Problems,
    IReadOnlyList<OrderingRuleGroupDto> Groups,
    IReadOnlyList<OrderingRuleOrderDto> Order);

/// <summary>Request to force a merge request into or out of a rollout.</summary>
/// <param name="State">
/// <c>auto</c> hands the decision back to the derivation, <c>included</c> forces it in,
/// <c>excluded</c> forces it out.
/// </param>
public record PlanMembershipRequest(string? State);
