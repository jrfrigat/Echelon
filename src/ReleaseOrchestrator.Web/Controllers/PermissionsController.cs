using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using ReleaseOrchestrator.Core.Entities;
using ReleaseOrchestrator.Infrastructure.Auth;
using ReleaseOrchestrator.Infrastructure.Persistence;

namespace ReleaseOrchestrator.Web.Controllers;

[ApiController]
[Route("api/permissions")]
[Authorize(Policy = Permissions.ConfigEdit)]
public class PermissionsController(AppDbContext db, IDistributedCache cache) : ControllerBase
{
    [HttpGet("claims")]
    public async Task<IActionResult> ListClaims(CancellationToken ct)
        => Ok(await db.PermissionClaims.Select(c => new { c.Id, c.Name }).ToListAsync(ct));

    [HttpGet("group-mappings")]
    public async Task<IActionResult> ListGroupMappings(CancellationToken ct)
        => Ok(await db.GroupPermissionMappings.Include(m => m.PermissionClaim)
            .Select(m => new { m.Id, m.AdGroupSid, ClaimName = m.PermissionClaim.Name })
            .ToListAsync(ct));

    [HttpPost("group-mappings")]
    public async Task<IActionResult> AddGroupMapping([FromBody] AddGroupMappingRequest req, CancellationToken ct)
    {
        db.GroupPermissionMappings.Add(new GroupPermissionMapping
        {
            Id = Guid.NewGuid(),
            AdGroupSid = req.AdGroupSid,
            PermissionClaimId = req.PermissionClaimId
        });
        await db.SaveChangesAsync(ct);
        await InvalidateCacheAsync();
        return NoContent();
    }

    [HttpDelete("group-mappings/{id:guid}")]
    public async Task<IActionResult> RemoveGroupMapping(Guid id, CancellationToken ct)
    {
        var entity = await db.GroupPermissionMappings.FindAsync([id], ct);
        if (entity is null) return NotFound();
        db.GroupPermissionMappings.Remove(entity);
        await db.SaveChangesAsync(ct);
        await InvalidateCacheAsync();
        return NoContent();
    }

    [HttpPost("user-overrides")]
    public async Task<IActionResult> AddUserOverride([FromBody] AddUserOverrideRequest req, CancellationToken ct)
    {
        db.UserPermissionOverrides.Add(new UserPermissionOverride
        {
            Id = Guid.NewGuid(),
            UserId = req.UserId,
            PermissionClaimId = req.PermissionClaimId
        });
        await db.SaveChangesAsync(ct);
        await InvalidateCacheAsync();
        return NoContent();
    }

    private async Task InvalidateCacheAsync()
    {
        // Increment the global permission version so per-user cache keys become stale.
        // PermissionClaimsTransformation prefixes keys with "perm:{version}:{userId}";
        // old keys expire naturally at their 5-minute TTL.
        var raw = await cache.GetStringAsync(PermissionClaimsTransformation.VersionCacheKey);
        var next = (long.TryParse(raw, out var v) ? v : 0L) + 1;
        await cache.SetStringAsync(PermissionClaimsTransformation.VersionCacheKey, next.ToString(),
            new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = TimeSpan.FromDays(365) });
    }
}

public record AddGroupMappingRequest(string AdGroupSid, Guid PermissionClaimId);
public record AddUserOverrideRequest(string UserId, Guid PermissionClaimId);
