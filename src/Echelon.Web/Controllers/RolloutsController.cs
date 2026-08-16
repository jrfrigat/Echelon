using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Echelon.Application.Services;
using Echelon.Infrastructure.Auth;

namespace Echelon.Web.Controllers;

/// <summary>
/// Rollout runs: their progress and the operator controls (cancel, retry, skip). Launching a rollout
/// lives on <see cref="TasksController"/> (<c>POST /api/tasks/{id}/rollouts</c>) because it is scoped
/// to a task.
/// </summary>
[ApiController]
[Route("api/rollouts")]
[Authorize(Policy = Permissions.ReleasePlanView)]
public class RolloutsController(IRolloutService rollouts) : ControllerBase
{
    /// <summary>Recent rollouts, newest first, optionally filtered to one task.</summary>
    /// <param name="taskId">The task to filter by, or omit for all.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] Guid? taskId, CancellationToken ct) =>
        Ok(await rollouts.ListAsync(taskId, ct));

    /// <summary>A rollout and its step timeline.</summary>
    /// <param name="id">The rollout id.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var rollout = await rollouts.GetAsync(id, ct);
        return rollout is null ? NotFound() : Ok(rollout);
    }

    /// <summary>Requests cancellation of a running rollout.</summary>
    /// <param name="id">The rollout id.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpPost("{id:guid}/cancel")]
    [Authorize(Policy = Permissions.ReleaseExecute)]
    public async Task<IActionResult> Cancel(Guid id, CancellationToken ct)
    {
        await rollouts.CancelAsync(id, this.ResolveActor(), ct);
        return NoContent();
    }

    /// <summary>Retries a failed step.</summary>
    /// <param name="id">The rollout id.</param>
    /// <param name="stepId">The step id.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpPost("{id:guid}/steps/{stepId:guid}/retry")]
    [Authorize(Policy = Permissions.ReleaseExecute)]
    public async Task<IActionResult> Retry(Guid id, Guid stepId, CancellationToken ct)
    {
        await rollouts.RetryStepAsync(id, stepId, this.ResolveActor(), ct);
        return NoContent();
    }

    /// <summary>Skips a step (marks its merge request skipped in the environment).</summary>
    /// <param name="id">The rollout id.</param>
    /// <param name="stepId">The step id.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpPost("{id:guid}/steps/{stepId:guid}/skip")]
    [Authorize(Policy = Permissions.ReleaseExecute)]
    public async Task<IActionResult> Skip(Guid id, Guid stepId, CancellationToken ct)
    {
        await rollouts.SkipStepAsync(id, stepId, this.ResolveActor(), ct);
        return NoContent();
    }
}
