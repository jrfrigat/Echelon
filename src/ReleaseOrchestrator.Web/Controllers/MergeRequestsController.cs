using Rebus.Bus;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using ReleaseOrchestrator.Application.Contracts.Messages;
using ReleaseOrchestrator.Application.Exceptions;
using ReleaseOrchestrator.Core.Enums;
using ReleaseOrchestrator.Core.Parsing;
using ReleaseOrchestrator.Infrastructure.Audit;
using ReleaseOrchestrator.Infrastructure.Auth;
using ReleaseOrchestrator.Infrastructure.Persistence;
using ReleaseOrchestrator.Web.Resources;

namespace ReleaseOrchestrator.Web.Controllers;

/// <summary>
/// Merge requests as this service holds them, and the one status change an operator can make by hand.
/// </summary>
/// <remarks>
/// Read-only apart from the status pin: merge requests are ingested from a VCS, never authored here.
/// </remarks>
[ApiController]
[Route("api/merge-requests")]
[Authorize(Policy = Permissions.ReleasePlanView)]
public class MergeRequestsController(
    AppDbContext db,
    IBus bus,
    TimeProvider clock,
    IStringLocalizer<ApiStrings> localizer) : ControllerBase
{
    /// <summary>Stored merge requests, newest first.</summary>
    /// <param name="status">Optional <see cref="MergeRequestStatus"/> name to filter by, case-insensitive.</param>
    /// <param name="page">1-based page.</param>
    /// <param name="pageSize">Page size, clamped by <see cref="Paging"/>.</param>
    /// <param name="ct">Cancellation token.</param>
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

    /// <summary>Every distinct label currently carried by a stored merge request.</summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Canonical label values, sorted.</returns>
    /// <remarks>
    /// Exists so configuring a readiness rule can offer the labels that actually exist instead of
    /// asking an operator to recall the exact spelling of one - a rule naming a label nothing carries
    /// is a gate nothing passes, and it fails silently at launch rather than when it was written.
    /// The labels are stored canonically (lower-cased, comma-joined), so distinct-then-split is
    /// enough; the split has to happen in memory because the column holds a joined set.
    /// </remarks>
    [HttpGet("labels")]
    public async Task<IActionResult> Labels(CancellationToken ct)
    {
        var sets = await db.MergeRequests
            .Where(mr => mr.Labels != "")
            .Select(mr => mr.Labels)
            .Distinct()
            .AsNoTracking()
            .ToListAsync(ct);

        return Ok(sets
            .SelectMany(s => s.Split(',', StringSplitOptions.RemoveEmptyEntries))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(l => l, StringComparer.Ordinal)
            .ToList());
    }

    /// <summary>Pins a merge request's status by hand.</summary>
    /// <param name="id">The merge request.</param>
    /// <param name="req">The status to pin. Merged and closed are rejected - the VCS owns those.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <remarks>
    /// The pin sets <c>IsStatusManual</c>, so a later observation does not re-derive the status over
    /// the top of it; a terminal state reported by the VCS still wins and clears the flag. This is no
    /// longer a route to making a merge request deployable - that became a per-environment readiness
    /// rule evaluated at launch, and a pin against it is <c>POST /api/readiness-pins</c>.
    /// </remarks>
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

        // A retired status is still parseable - the members stay declared so old rows materialise -
        // but storing one now leaves the merge request somewhere nothing can act on: the value means
        // nothing to any reader, and the manual-status flag this sets stops observation from ever
        // correcting it. Refused with the thing to do instead.
        if (MergeRequestStatusResolver.IsRetired(status))
            return BadRequest(new { error = localizer["Mr_StatusRetired", status].Value });

        var mr = await db.MergeRequests.FirstOrDefaultAsync(m => m.Id == id, ct)
            ?? throw new NotFoundException(localizer["Mr_NotFound", id]);

        if (MergeRequestStatusResolver.IsTerminal(mr.Status))
            return BadRequest(new { error = localizer["Mr_StatusFinal", mr.Status].Value });

        // The one status change with a person behind it. IsStatusManual records that somebody
        // intervened but not who or when, and it is cleared by the next terminal VCS event -- so
        // without this row the intervention disappears from the history the moment the MR merges.
        MergeRequestStatusJournal.Record(
            db, mr, mr.Status, status,
            MergeRequestStatusJournal.CauseManualPin, this.ResolveActor(), clock.GetUtcNow().UtcDateTime);

        mr.Status = status;
        mr.IsStatusManual = true;
        await db.SaveChangesAsync(ct);

        await bus.Send(new ReleasePlanRecalculationRequested(
            clock.GetUtcNow().UtcDateTime, $"MR {mr.ExternalId} status set manually to {status}"));

        return NoContent();
    }
}

/// <summary>The status an operator is pinning.</summary>
/// <param name="Status">A <see cref="MergeRequestStatus"/> name, case-insensitive. Non-terminal only.</param>
public record SetMrStatusRequest(string Status);
