using MassTransit;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using ReleaseOrchestrator.Application.Contracts.Messages;
using ReleaseOrchestrator.Application.Exceptions;
using ReleaseOrchestrator.Core.Enums;
using ReleaseOrchestrator.Core.Parsing;
using ReleaseOrchestrator.Infrastructure.Auth;
using ReleaseOrchestrator.Infrastructure.Persistence;
using ReleaseOrchestrator.Web.Resources;

namespace ReleaseOrchestrator.Web.Controllers;

[ApiController]
[Route("api/merge-requests")]
[Authorize(Policy = Permissions.ReleasePlanView)]
public class MergeRequestsController(
    AppDbContext db,
    IPublishEndpoint publisher,
    TimeProvider clock,
    IStringLocalizer<ApiStrings> localizer) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] string? status,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = Paging.DefaultPageSize,
        CancellationToken ct = default)
    {
        var paging = Paging.From(page, pageSize);

        var query = db.MergeRequests
            .Include(mr => mr.Repository).ThenInclude(r => r.Connection)
            .Include(mr => mr.Task)
            .AsQueryable();

        if (!string.IsNullOrEmpty(status))
        {
            if (!Enum.TryParse<MergeRequestStatus>(status, true, out var parsed))
                return BadRequest(new { error = localizer["Mr_UnknownStatus", status, string.Join(", ", Enum.GetNames<MergeRequestStatus>())].Value });

            query = query.Where(mr => mr.Status == parsed);
        }

        var total = await query.CountAsync(ct);
        var items = await query
            .OrderByDescending(mr => mr.CreatedAt).ThenBy(mr => mr.Id)
            .Skip(paging.Skip)
            .Take(paging.PageSize)
            .Select(mr => new
            {
                mr.Id,
                mr.ExternalId,
                mr.SourceBranch,
                mr.TargetBranch,
                mr.Status,
                mr.CreatedAt,
                mr.MergedAt,
                mr.ClosedAt,
                mr.IsStatusManual,
                mr.RepositoryId,
                RepositoryName = mr.Repository.Name,
                ConnectionName = mr.Repository.Connection.Name,
                TaskExternalId = mr.Task != null ? mr.Task.ExternalId : null
            })
            .AsSplitQuery()
            .ToListAsync(ct);

        return Ok(new { Total = total, Page = paging.Page, PageSize = paging.PageSize, Items = items });
    }

    /// <summary>
    /// Pins a merge request's status by hand — the second of the two routes into the plan
    /// alongside the connection's ready-for-deploy label (README §5). The pin survives later
    /// label-driven updates; a terminal state reported by the VCS clears it.
    /// </summary>
    [HttpPatch("{id:guid}/status")]
    [Authorize(Policy = Permissions.ReleasePlanApprove)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> SetStatus(Guid id, [FromBody] SetMrStatusRequest req, CancellationToken ct)
    {
        if (!Enum.TryParse<MergeRequestStatus>(req.Status, true, out var status))
            return BadRequest(new { error = localizer["Mr_UnknownStatus", req.Status, string.Join(", ", Enum.GetNames<MergeRequestStatus>())].Value });

        // Merged/closed are facts reported by the VCS, not decisions an operator makes here.
        if (MergeRequestStatusResolver.IsTerminal(status))
            return BadRequest(new { error = localizer["Mr_StatusVcsOwned", status].Value });

        var mr = await db.MergeRequests.FirstOrDefaultAsync(m => m.Id == id, ct)
            ?? throw new NotFoundException(localizer["Mr_NotFound", id]);

        if (MergeRequestStatusResolver.IsTerminal(mr.Status))
            return BadRequest(new { error = localizer["Mr_StatusFinal", mr.Status].Value });

        mr.Status = status;
        mr.IsStatusManual = true;
        await db.SaveChangesAsync(ct);

        await publisher.Publish(new ReleasePlanRecalculationRequested(
            clock.GetUtcNow().UtcDateTime, $"MR {mr.ExternalId} status set manually to {status}"), ct);

        return NoContent();
    }
}

public record SetMrStatusRequest(string Status);
