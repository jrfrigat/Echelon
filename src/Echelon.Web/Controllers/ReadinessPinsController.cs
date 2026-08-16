using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Echelon.Infrastructure.Auth;
using Echelon.Infrastructure.Persistence;
using Echelon.Infrastructure.Persistence.Models;

namespace Echelon.Web.Controllers;

/// <summary>
/// A person's readiness decisions for merge requests, overriding what their labels would say.
/// </summary>
/// <remarks>
/// The escape hatch the label gate needs: a merged merge request has left the poller's open-request
/// listing, so a <c>ready-for-prod</c> approval that lands after the merge may never be observable
/// from labels -- and without a pin the first production rule would wedge every task holding it.
/// Because a pin can also deny, it doubles as a hold. Gated on approval permission, not config edit:
/// admitting a merge request into an environment its labels would refuse is exactly an approval.
/// </remarks>
[ApiController]
[Route("api/readiness-pins")]
[Authorize(Policy = Permissions.ReleasePlanApprove)]
public class ReadinessPinsController(AppDbContext db, TimeProvider clock) : ControllerBase
{
    /// <summary>Lists the readiness pins for one merge request, across environments.</summary>
    /// <param name="mergeRequestId">The merge request whose pins to list.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet]
    public async Task<IActionResult> List([FromQuery, Required] Guid mergeRequestId, CancellationToken ct)
    {
        var items = await db.MergeRequestReadinessPins
            .Where(p => p.MergeRequestId == mergeRequestId)
            .OrderBy(p => p.Environment.Order)
            .Select(p => new
            {
                p.Id, p.MergeRequestId, p.EnvironmentId,
                EnvironmentKey = p.Environment.Key,
                p.IsReady, p.Reason, p.ActorName, p.At
            })
            .ToListAsync(ct);
        return Ok(items);
    }

    /// <summary>
    /// Pins a merge request ready or not-ready for an environment, replacing any existing pin for
    /// that pair.
    /// </summary>
    /// <param name="req">The pin.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpPut]
    public async Task<IActionResult> Set([FromBody] SetReadinessPinRequest req, CancellationToken ct)
    {
        if (!await db.MergeRequests.AnyAsync(m => m.Id == req.MergeRequestId, ct))
            return BadRequest(new { error = "No such merge request." });
        if (!await db.DeploymentEnvironments.AnyAsync(e => e.Id == req.EnvironmentId, ct))
            return BadRequest(new { error = "No such environment." });

        var actor = this.ResolveActor();
        var now = clock.GetUtcNow().UtcDateTime;

        // Upsert on the (merge request, environment) pair -- a person changing their mind updates the
        // decision rather than stacking a second pin, and the unique index on the pair would reject
        // one anyway.
        var pin = await db.MergeRequestReadinessPins
            .FirstOrDefaultAsync(p => p.MergeRequestId == req.MergeRequestId && p.EnvironmentId == req.EnvironmentId, ct);

        if (pin is null)
        {
            pin = new MergeRequestReadinessPin
            {
                Id = Guid.NewGuid(),
                MergeRequestId = req.MergeRequestId,
                EnvironmentId = req.EnvironmentId
            };
            db.MergeRequestReadinessPins.Add(pin);
        }

        pin.IsReady = req.IsReady;
        pin.Reason = string.IsNullOrWhiteSpace(req.Reason) ? null : req.Reason.Trim();
        pin.ActorOid = actor.Oid;
        pin.ActorKind = actor.Kind;
        pin.ActorName = actor.DisplayName;
        pin.At = now;

        await db.SaveChangesAsync(ct);
        return Ok(new { pin.Id });
    }

    /// <summary>
    /// Removes a pin, so the merge request's readiness for that environment reverts to its labels.
    /// </summary>
    /// <param name="id">The pin id.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var pin = await db.MergeRequestReadinessPins.FindAsync([id], ct);
        if (pin is null) return NotFound();

        db.MergeRequestReadinessPins.Remove(pin);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }
}

/// <param name="IsReady">True admits the merge request into the environment; false holds it out.</param>
/// <param name="Reason">Why, in the operator's words. Optional; shown in the readiness history.</param>
public record SetReadinessPinRequest(
    [Required] Guid MergeRequestId,
    [Required] Guid EnvironmentId,
    bool IsReady,
    [MaxLength(500)] string? Reason = null);
