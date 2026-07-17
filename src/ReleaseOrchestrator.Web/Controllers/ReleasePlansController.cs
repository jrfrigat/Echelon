using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ReleaseOrchestrator.Application.DTOs;
using ReleaseOrchestrator.Application.Services;
using ReleaseOrchestrator.Infrastructure.Auth;

namespace ReleaseOrchestrator.Web.Controllers;

[ApiController]
[Route("api/release-plans")]
[Authorize(Policy = Permissions.ReleasePlanView)]
public class ReleasePlansController(IReleasePlannerService planner) : ControllerBase
{
    [HttpGet("active")]
    [ProducesResponseType<ReleasePlanDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetActive(CancellationToken ct)
    {
        var plan = await planner.GetActiveAsync(ct);
        return plan is null ? NotFound() : Ok(plan);
    }

    [HttpGet("{id:guid}")]
    [ProducesResponseType<ReleasePlanDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetById(Guid id, CancellationToken ct)
    {
        var plan = await planner.GetByIdAsync(id, ct);
        return plan is null ? NotFound() : Ok(plan);
    }

    // Recalculation is a heavy full rebuild; leaving it on bare [Authorize] let any
    // authenticated user drive it in a loop.
    [HttpPost("recalculate")]
    [Authorize(Policy = Permissions.ReleasePlanApprove)]
    [ProducesResponseType<ReleasePlanDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Recalculate(CancellationToken ct)
    {
        var plan = await planner.RecalculateAsync(ct);
        return Ok(plan);
    }

    // Import replaces the active plan wholesale — the single most destructive operation
    // here, and the only mutating one that used to require no permission at all.
    [HttpPost("import")]
    [Authorize(Policy = Permissions.ReleasePlanApprove)]
    [ProducesResponseType<ReleasePlanDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ImportYaml([FromBody] ImportYamlRequest request, CancellationToken ct)
    {
        var plan = await planner.ImportFromYamlAsync(request.Yaml, request.Force, ct);
        return Ok(plan);
    }

    [HttpGet("{id:guid}/export")]
    [ProducesResponseType<string>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ExportYaml(Guid id, CancellationToken ct)
    {
        var yaml = await planner.ExportToYamlAsync(id, ct);
        return Content(yaml, "application/yaml");
    }

    [HttpPatch("{id:guid}/stages/reorder")]
    [Authorize(Policy = Permissions.ReleasePlanApprove)]
    [ProducesResponseType<ReleasePlanDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ReorderStages(Guid id, [FromBody] ReorderStagesRequest req, CancellationToken ct)
    {
        var plan = await planner.ReorderStagesAsync(id, req.StageIds, ct);
        return Ok(plan);
    }

    [HttpPost("{id:guid}/stages/{stageId:guid}/items")]
    [Authorize(Policy = Permissions.ReleasePlanApprove)]
    [ProducesResponseType<ReleasePlanDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> AddItem(Guid id, Guid stageId, [FromBody] AddStageItemRequest req, CancellationToken ct)
    {
        var plan = await planner.AddItemAsync(id, stageId, req.MergeRequestId, ct);
        return Ok(plan);
    }

    [HttpDelete("{id:guid}/items/{itemId:guid}")]
    [Authorize(Policy = Permissions.ReleasePlanApprove)]
    [ProducesResponseType<ReleasePlanDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RemoveItem(Guid id, Guid itemId, CancellationToken ct)
    {
        var plan = await planner.RemoveItemAsync(id, itemId, ct);
        return Ok(plan);
    }

    [HttpPost("{id:guid}/items/{itemId:guid}/move")]
    [Authorize(Policy = Permissions.ReleasePlanApprove)]
    [ProducesResponseType<ReleasePlanDto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> MoveItem(Guid id, Guid itemId, [FromBody] MoveItemRequest req, CancellationToken ct)
    {
        var plan = await planner.MoveItemAsync(id, itemId, req.TargetStageId, ct);
        return Ok(plan);
    }
}

/// <param name="Yaml">The plan document.</param>
/// <param name="Force">
/// Skip <c>mr_id</c>s that resolve to nothing instead of refusing the document. It does not
/// override dependency checks: a plan that breaks one is accepted either way and reports the
/// violation in its conflicts.
/// </param>
public record ImportYamlRequest(
    // Kestrel's 30 MB default body limit is far too generous for a plan document, and the
    // parser holds the whole thing in memory.
    [property: Required, MaxLength(2 * 1024 * 1024)] string Yaml,
    bool Force = false);
public record ReorderStagesRequest(List<Guid> StageIds);
public record AddStageItemRequest(Guid MergeRequestId);
public record MoveItemRequest(Guid TargetStageId);
