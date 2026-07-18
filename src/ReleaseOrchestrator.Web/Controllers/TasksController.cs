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
public class TasksController(IRolloutPlannerService planner, IRolloutService rollouts) : ControllerBase
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

    /// <summary>The active rollout plan for a task (its dependency tree and execution waves).</summary>
    /// <param name="id">The target task id.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet("{id:guid}/plan")]
    public async Task<IActionResult> GetPlan(Guid id, CancellationToken ct)
    {
        var plan = await planner.GetActivePlanAsync(id, ct);
        return plan is null ? NotFound() : Ok(plan);
    }

    /// <summary>Rebuilds the task's plan from the atlas and makes it active.</summary>
    /// <param name="id">The target task id.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpPost("{id:guid}/plan/recalculate")]
    [Authorize(Policy = Permissions.ReleasePlanApprove)]
    public async Task<IActionResult> Recalculate(Guid id, CancellationToken ct)
    {
        var plan = await planner.RecalculateAsync(id, ct);
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
        var launchedByOid = UserIdentifier.TryResolve(User, out var oid) ? oid : null;
        var rollout = await rollouts.LaunchAsync(id, req.EnvironmentId, launchedByOid, ct);
        return Ok(rollout);
    }
}

/// <summary>Request to launch a rollout.</summary>
/// <param name="EnvironmentId">The target environment.</param>
public record LaunchRolloutRequest([property: Required] Guid EnvironmentId);
