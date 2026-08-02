using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.ComponentModel.DataAnnotations;
using ReleaseOrchestrator.Application.Services;
using ReleaseOrchestrator.Infrastructure.Auth;

namespace ReleaseOrchestrator.Web.Controllers;

/// <summary>
/// Tasks and their rollout plans -- the object the pivoted product is built around. An operator
/// lists tasks, opens one, sees its dependency tree, and launches its rollout.
/// </summary>
[ApiController]
[Route("api/tasks")]
[Authorize(Policy = Permissions.ReleasePlanView)]
public class TasksController(
    IRolloutPlannerService planner,
    IRolloutService rollouts,
    ITaskTimelineService timeline) : ControllerBase
{
    /// <summary>Lists tasks, paged.</summary>
    /// <param name="page">1-based page number.</param>
    /// <param name="pageSize">Page size, clamped by <see cref="Paging"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] int page = 1, [FromQuery] int pageSize = Paging.DefaultPageSize, CancellationToken ct = default)
    {
        var paging = Paging.From(page, pageSize);
        var total = await planner.CountTasksAsync(ct);
        var items = await planner.ListTasksAsync(paging.Page, paging.PageSize, ct);
        return Ok(new { Total = total, paging.Page, paging.PageSize, Items = items });
    }

    /// <summary>One task's own facts and its place in the tracker hierarchy (parent and subtasks).</summary>
    /// <param name="id">The task id.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var task = await planner.GetTaskAsync(id, ct);
        return task is null ? NotFound() : Ok(task);
    }

    /// <summary>
    /// Everything that has happened to this task: arrival, merge-request changes, plan versions and
    /// who caused them, and every rollout with its launcher and environment.
    /// </summary>
    /// <param name="id">The task id.</param>
    /// <param name="limit">Maximum entries per source before the answer reports itself truncated.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <remarks>
    /// Guarded by the same view policy as the rest of this controller. The data is the history of
    /// facts an operator can already see individually — this assembles them; it does not expose
    /// anything a plan view does not.
    /// </remarks>
    [HttpGet("{id:guid}/timeline")]
    public async Task<IActionResult> Timeline(Guid id, [FromQuery] int limit = 200, CancellationToken ct = default)
    {
        var result = await timeline.GetAsync(id, Math.Clamp(limit, 1, 500), ct);
        return result is null ? NotFound() : Ok(result);
    }

    /// <summary>The active rollout plan for a task (its dependency tree and execution waves).</summary>
    /// <param name="id">The target task id.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet("{id:guid}/plan")]
    public async Task<IActionResult> GetPlan(Guid id, CancellationToken ct)
    {
        var plan = await planner.GetActivePlanAsync(id, ct);
        return plan is null ? NotFound() : Ok(plan);
    }

    /// <summary>The task's active plan as a YAML document.</summary>
    /// <param name="id">The target task.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The plan in the per-task schema, or 404 when the task has no active plan.</returns>
    /// <remarks>
    /// Export only — there is no import. A plan is a projection of the atlas, so a round trip would
    /// have to go through the ordering rules and the plan overrides rather than through this text;
    /// what this is for is reading, diffing and attaching to a change record.
    /// </remarks>
    [HttpGet("{id:guid}/plan/export")]
    public async Task<IActionResult> ExportPlan(Guid id, CancellationToken ct)
    {
        var yaml = await planner.ExportPlanYamlAsync(id, ct);

        // text/yaml so a browser shows it and curl can pipe it, rather than JSON-escaping a document
        // whose whole point is being readable.
        return yaml is null ? NotFound() : Content(yaml, "text/yaml; charset=utf-8");
    }

    /// <summary>Rebuilds the task's plan from the atlas and makes it active.</summary>
    /// <param name="id">The target task id.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpPost("{id:guid}/plan/recalculate")]
    [Authorize(Policy = Permissions.ReleasePlanApprove)]
    public async Task<IActionResult> Recalculate(Guid id, CancellationToken ct)
    {
        var plan = await planner.RecalculateAsync(id, this.ResolveActor(), ct);
        return Ok(plan);
    }

    /// <summary>Launches a rollout of this task into an environment.</summary>
    /// <param name="id">The target task id.</param>
    /// <param name="req">The launch request (target environment).</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpPost("{id:guid}/rollouts")]
    [Authorize(Policy = Permissions.ReleaseExecute)]
    public async Task<IActionResult> Launch(Guid id, [FromBody] LaunchRolloutRequest req, CancellationToken ct)
    {
        var rollout = await rollouts.LaunchAsync(id, req.EnvironmentId, this.ResolveActor(), req.Redeploy, ct);
        return Ok(rollout);
    }
}

/// <summary>Request to launch a rollout.</summary>
/// <param name="EnvironmentId">The target environment.</param>
/// <param name="Redeploy">
/// When true, redeploy merge requests already deployed to this environment — honoured only where the
/// repository's deploy target for the environment sets <c>RedeployPolicy.Always</c>.
/// </param>
public record LaunchRolloutRequest([Required] Guid EnvironmentId, bool Redeploy = false);
