using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ReleaseOrchestrator.Application.DTOs;
using ReleaseOrchestrator.Application.Services;
using ReleaseOrchestrator.Infrastructure.Auth;

namespace ReleaseOrchestrator.Web.Controllers;

[ApiController]
[Route("api/release-plans")]
[Authorize]
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

    [HttpPost("recalculate")]
    [ProducesResponseType<ReleasePlanDto>(StatusCodes.Status200OK)]
    public async Task<IActionResult> Recalculate(CancellationToken ct)
    {
        var plan = await planner.RecalculateAsync(ct);
        return Ok(plan);
    }

    [HttpPost("import")]
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

public record ImportYamlRequest(string Yaml, bool Force = false);
public record ReorderStagesRequest(List<Guid> StageIds);
public record AddStageItemRequest(Guid MergeRequestId);
public record MoveItemRequest(Guid TargetStageId);
