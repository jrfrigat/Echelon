using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Localization;
using ReleaseOrchestrator.Infrastructure.Persistence.Models;
using ReleaseOrchestrator.Infrastructure.Auth;
using ReleaseOrchestrator.Infrastructure.Persistence;
using ReleaseOrchestrator.Web.Resources;

namespace ReleaseOrchestrator.Web.Controllers;

/// <summary>
/// Every mutation here is audited: <see cref="Permissions.ConfigEdit"/> lets a holder grant
/// <see cref="Permissions.ConfigEdit"/> to anyone, themselves included, so who granted what to whom
/// has to survive the request for an escalation to be traceable afterwards.
/// </summary>
[ApiController]
[Route("api/permissions")]
[Authorize(Policy = Permissions.ConfigEdit)]
public class PermissionsController(
    AppDbContext db,
    IDistributedCache cache,
    ILogger<PermissionsController> logger,
    IStringLocalizer<ApiStrings> localizer) : ControllerBase
{
    // Delegates to the one resolution path rather than keeping a second: this controller's own
    // version predates the audit trail, and two answers to "who is calling" eventually disagree.
    // The NameIdentifier fallback is preserved -- for an unresolvable principal it is still the
    // most identifying thing in the token, and these are permission-grant log lines.
    private string ActorId => this.ResolveActor().Oid
        ?? User.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? "unknown";

    private string ActorName => this.ResolveActor().DisplayName ?? "unknown";

    /// <summary>Every permission claim the service defines. The vocabulary a grant names.</summary>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet("claims")]
    public async Task<IActionResult> ListClaims(CancellationToken ct)
        => Ok(await db.PermissionClaims
            .OrderBy(c => c.Name)
            .Select(c => new { c.Id, c.Name })
            .ToListAsync(ct));

    /// <summary>Which directory group holds which permission.</summary>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet("group-mappings")]
    public async Task<IActionResult> ListGroupMappings(CancellationToken ct)
        => Ok(await db.GroupPermissionMappings
            .OrderBy(m => m.AdGroupSid).ThenBy(m => m.PermissionClaim.Name)
            .Select(m => new { m.Id, m.AdGroupSid, ClaimName = m.PermissionClaim.Name })
            .ToListAsync(ct));

    /// <summary>Grants a permission to a directory group.</summary>
    /// <param name="req">The group and the claim to grant it.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>204, or 409 when the group already holds the claim, or 400 when the claim is unknown.</returns>
    [HttpPost("group-mappings")]
    public async Task<IActionResult> AddGroupMapping([FromBody] AddGroupMappingRequest req, CancellationToken ct)
    {
        var claimName = await ClaimNameAsync(req.PermissionClaimId, ct);
        if (claimName is null)
            return BadRequest(new { error = localizer["Perm_ClaimNotFound", req.PermissionClaimId].Value });

        if (await db.GroupPermissionMappings.AnyAsync(
                m => m.AdGroupSid == req.AdGroupSid && m.PermissionClaimId == req.PermissionClaimId, ct))
            return Conflict(new { error = localizer["Perm_GroupAlreadyHolds", req.AdGroupSid, claimName].Value });

        db.GroupPermissionMappings.Add(new GroupPermissionMapping
        {
            Id = Guid.NewGuid(),
            AdGroupSid = req.AdGroupSid,
            PermissionClaimId = req.PermissionClaimId
        });

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Lost the race with a concurrent grant. The check above is advisory — the unique index
            // is the guarantee — so the answer is the one the check would have given, not a 500.
            // Anything else that broke the write is not ours to swallow.
            db.ChangeTracker.Clear();
            if (!await db.GroupPermissionMappings.AnyAsync(
                    m => m.AdGroupSid == req.AdGroupSid && m.PermissionClaimId == req.PermissionClaimId, ct))
                throw;

            return Conflict(new { error = localizer["Perm_GroupAlreadyHolds", req.AdGroupSid, claimName].Value });
        }

        await PermissionVersionStore.InvalidateAsync(cache, ct);

        logger.LogInformation(
            "Permission granted: actor {ActorId} ({ActorName}) granted {PermissionClaim} to group {AdGroupSid}.",
            ActorId, ActorName, claimName, req.AdGroupSid);

        return NoContent();
    }

    /// <summary>Revokes a group's permission.</summary>
    /// <param name="id">The mapping to remove.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpDelete("group-mappings/{id:guid}")]
    public async Task<IActionResult> RemoveGroupMapping(Guid id, CancellationToken ct)
    {
        var entity = await db.GroupPermissionMappings
            .Include(m => m.PermissionClaim)
            .FirstOrDefaultAsync(m => m.Id == id, ct);
        if (entity is null) return NotFound();

        db.GroupPermissionMappings.Remove(entity);
        await db.SaveChangesAsync(ct);
        await PermissionVersionStore.InvalidateAsync(cache, ct);

        logger.LogInformation(
            "Permission revoked: actor {ActorId} ({ActorName}) revoked {PermissionClaim} from group {AdGroupSid}.",
            ActorId, ActorName, entity.PermissionClaim.Name, entity.AdGroupSid);

        return NoContent();
    }

    /// <summary>Permissions granted to an individual, on top of whatever their groups carry.</summary>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet("user-overrides")]
    public async Task<IActionResult> ListUserOverrides(CancellationToken ct)
        => Ok(await db.UserPermissionOverrides
            .OrderBy(o => o.UserId).ThenBy(o => o.PermissionClaim.Name)
            .Select(o => new { o.Id, o.UserId, ClaimName = o.PermissionClaim.Name })
            .ToListAsync(ct));

    /// <summary>Grants a permission to one user directly.</summary>
    /// <param name="req">The user's object id and the claim to grant.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// 204, or 409 when the user already holds the claim, or 400 when the claim is unknown or the
    /// user id is not an object id.
    /// </returns>
    [HttpPost("user-overrides")]
    public async Task<IActionResult> AddUserOverride([FromBody] AddUserOverrideRequest req, CancellationToken ct)
    {
        // PermissionClaimsTransformation matches overrides on the object id from the token, so
        // anything else an administrator might reach for — a UPN, an email, a display name — would
        // store a row that quietly never applies.
        if (!UserIdentifier.TryNormalize(req.UserId, out var userId))
            return BadRequest(new { error = localizer["Perm_UserIdMustBeOid"].Value });

        var claimName = await ClaimNameAsync(req.PermissionClaimId, ct);
        if (claimName is null)
            return BadRequest(new { error = localizer["Perm_ClaimNotFound", req.PermissionClaimId].Value });

        if (await db.UserPermissionOverrides.AnyAsync(
                o => o.UserId == userId && o.PermissionClaimId == req.PermissionClaimId, ct))
            return Conflict(new { error = localizer["Perm_UserAlreadyHolds", userId, claimName].Value });

        db.UserPermissionOverrides.Add(new UserPermissionOverride
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            PermissionClaimId = req.PermissionClaimId
        });

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // See the group grant above: the index decides, and losing to it is a conflict.
            db.ChangeTracker.Clear();
            if (!await db.UserPermissionOverrides.AnyAsync(
                    o => o.UserId == userId && o.PermissionClaimId == req.PermissionClaimId, ct))
                throw;

            return Conflict(new { error = localizer["Perm_UserAlreadyHolds", userId, claimName].Value });
        }

        await PermissionVersionStore.InvalidateAsync(cache, ct);

        logger.LogInformation(
            "Permission granted: actor {ActorId} ({ActorName}) granted {PermissionClaim} to user {TargetUserId}.",
            ActorId, ActorName, claimName, userId);

        return NoContent();
    }

    /// <summary>Revokes a user's direct permission. Their groups' grants are untouched.</summary>
    /// <param name="id">The override to remove.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpDelete("user-overrides/{id:guid}")]
    public async Task<IActionResult> RemoveUserOverride(Guid id, CancellationToken ct)
    {
        var entity = await db.UserPermissionOverrides
            .Include(o => o.PermissionClaim)
            .FirstOrDefaultAsync(o => o.Id == id, ct);
        if (entity is null) return NotFound();

        db.UserPermissionOverrides.Remove(entity);
        await db.SaveChangesAsync(ct);
        await PermissionVersionStore.InvalidateAsync(cache, ct);

        logger.LogInformation(
            "Permission revoked: actor {ActorId} ({ActorName}) revoked {PermissionClaim} from user {TargetUserId}.",
            ActorId, ActorName, entity.PermissionClaim.Name, entity.UserId);

        return NoContent();
    }

    private Task<string?> ClaimNameAsync(Guid permissionClaimId, CancellationToken ct)
        => db.PermissionClaims
            .Where(c => c.Id == permissionClaimId)
            .Select(c => c.Name)
            .FirstOrDefaultAsync(ct);
}

/// <summary>Grants a permission claim to a directory group.</summary>
/// <param name="AdGroupSid">The group's security identifier, as the token carries it.</param>
/// <param name="PermissionClaimId">The claim to grant.</param>
public record AddGroupMappingRequest(
    [Required, MaxLength(200)] string AdGroupSid,
    Guid PermissionClaimId);

/// <summary>Grants a permission claim to one user directly.</summary>
/// <param name="UserId">Entra ID object id (oid), as a GUID. A UPN or an email is rejected.</param>
/// <param name="PermissionClaimId">The claim to grant.</param>
public record AddUserOverrideRequest(
    [Required, MaxLength(450)] string UserId,
    Guid PermissionClaimId);
