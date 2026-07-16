using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using ReleaseOrchestrator.Core.Entities;
using ReleaseOrchestrator.Infrastructure.Auth;
using ReleaseOrchestrator.Infrastructure.Persistence;

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
    ILogger<PermissionsController> logger) : ControllerBase
{
    private string ActorId => UserIdentifier.TryResolve(User, out var objectId)
        ? objectId
        : User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "unknown";

    private string ActorName =>
        User.FindFirstValue("preferred_username") ?? User.Identity?.Name ?? "unknown";

    [HttpGet("claims")]
    public async Task<IActionResult> ListClaims(CancellationToken ct)
        => Ok(await db.PermissionClaims
            .OrderBy(c => c.Name)
            .Select(c => new { c.Id, c.Name })
            .ToListAsync(ct));

    [HttpGet("group-mappings")]
    public async Task<IActionResult> ListGroupMappings(CancellationToken ct)
        => Ok(await db.GroupPermissionMappings
            .OrderBy(m => m.AdGroupSid).ThenBy(m => m.PermissionClaim.Name)
            .Select(m => new { m.Id, m.AdGroupSid, ClaimName = m.PermissionClaim.Name })
            .ToListAsync(ct));

    [HttpPost("group-mappings")]
    public async Task<IActionResult> AddGroupMapping([FromBody] AddGroupMappingRequest req, CancellationToken ct)
    {
        var claimName = await ClaimNameAsync(req.PermissionClaimId, ct);
        if (claimName is null)
            return BadRequest(new { error = $"Permission claim '{req.PermissionClaimId}' does not exist." });

        if (await db.GroupPermissionMappings.AnyAsync(
                m => m.AdGroupSid == req.AdGroupSid && m.PermissionClaimId == req.PermissionClaimId, ct))
            return Conflict(new { error = $"Group '{req.AdGroupSid}' already holds '{claimName}'." });

        db.GroupPermissionMappings.Add(new GroupPermissionMapping
        {
            Id = Guid.NewGuid(),
            AdGroupSid = req.AdGroupSid,
            PermissionClaimId = req.PermissionClaimId
        });
        await db.SaveChangesAsync(ct);
        await PermissionVersionStore.InvalidateAsync(cache, ct);

        logger.LogInformation(
            "Permission granted: actor {ActorId} ({ActorName}) granted {PermissionClaim} to group {AdGroupSid}.",
            ActorId, ActorName, claimName, req.AdGroupSid);

        return NoContent();
    }

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

    [HttpGet("user-overrides")]
    public async Task<IActionResult> ListUserOverrides(CancellationToken ct)
        => Ok(await db.UserPermissionOverrides
            .OrderBy(o => o.UserId).ThenBy(o => o.PermissionClaim.Name)
            .Select(o => new { o.Id, o.UserId, ClaimName = o.PermissionClaim.Name })
            .ToListAsync(ct));

    [HttpPost("user-overrides")]
    public async Task<IActionResult> AddUserOverride([FromBody] AddUserOverrideRequest req, CancellationToken ct)
    {
        // PermissionClaimsTransformation matches overrides on the object id from the token, so
        // anything else an administrator might reach for — a UPN, an email, a display name — would
        // store a row that quietly never applies.
        if (!UserIdentifier.TryNormalize(req.UserId, out var userId))
            return BadRequest(new { error = "userId must be the Entra ID object id (oid) — a GUID." });

        var claimName = await ClaimNameAsync(req.PermissionClaimId, ct);
        if (claimName is null)
            return BadRequest(new { error = $"Permission claim '{req.PermissionClaimId}' does not exist." });

        if (await db.UserPermissionOverrides.AnyAsync(
                o => o.UserId == userId && o.PermissionClaimId == req.PermissionClaimId, ct))
            return Conflict(new { error = $"User '{userId}' already holds '{claimName}'." });

        db.UserPermissionOverrides.Add(new UserPermissionOverride
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            PermissionClaimId = req.PermissionClaimId
        });
        await db.SaveChangesAsync(ct);
        await PermissionVersionStore.InvalidateAsync(cache, ct);

        logger.LogInformation(
            "Permission granted: actor {ActorId} ({ActorName}) granted {PermissionClaim} to user {TargetUserId}.",
            ActorId, ActorName, claimName, userId);

        return NoContent();
    }

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

public record AddGroupMappingRequest(
    [property: Required, MaxLength(200)] string AdGroupSid,
    Guid PermissionClaimId);

public record AddUserOverrideRequest(
    /// <summary>Entra ID object id (oid).</summary>
    [property: Required, MaxLength(450)] string UserId,
    Guid PermissionClaimId);
