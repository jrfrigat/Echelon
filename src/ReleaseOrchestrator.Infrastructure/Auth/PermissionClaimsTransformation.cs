using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Logging;
using ReleaseOrchestrator.Infrastructure.Persistence;
using System.Text.Json;

namespace ReleaseOrchestrator.Infrastructure.Auth;

public class PermissionClaimsTransformation(
    AppDbContext db,
    IDistributedCache cache,
    ILogger<PermissionClaimsTransformation> logger) : IClaimsTransformation
{
    public const string VersionCacheKey = "perm:version";

    private static readonly DistributedCacheEntryOptions CacheOptions = new()
    {
        AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5)
    };

    public async Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        if (principal.Identity?.IsAuthenticated != true) return principal;

        var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier) ?? principal.FindFirstValue("oid") ?? string.Empty;
        var groupSids = principal.FindAll("groups").Select(c => c.Value).ToList();

        var permissions = await GetPermissionsAsync(userId, groupSids);

        var identity = new ClaimsIdentity();
        identity.AddClaims(permissions.Select(p => new Claim("permission", p)));
        principal.AddIdentity(identity);
        return principal;
    }

    private async Task<IReadOnlyList<string>> GetPermissionsAsync(string userId, List<string> groupSids)
    {
        var version = await cache.GetStringAsync(VersionCacheKey) ?? "0";
        var cacheKey = $"perm:{version}:{userId}";
        var cached = await cache.GetStringAsync(cacheKey);
        if (cached is not null)
            return JsonSerializer.Deserialize<List<string>>(cached) ?? [];

        try
        {
            var permissions = new HashSet<string>();

            var groupPerms = await db.GroupPermissionMappings
                .Include(m => m.PermissionClaim)
                .Where(m => groupSids.Contains(m.AdGroupSid))
                .Select(m => m.PermissionClaim.Name)
                .ToListAsync();

            foreach (var p in groupPerms) permissions.Add(p);

            var userPerms = await db.UserPermissionOverrides
                .Include(o => o.PermissionClaim)
                .Where(o => o.UserId == userId)
                .Select(o => o.PermissionClaim.Name)
                .ToListAsync();

            foreach (var p in userPerms) permissions.Add(p);

            var result = permissions.ToList();
            await cache.SetStringAsync(cacheKey, JsonSerializer.Serialize(result), CacheOptions);
            return result;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to load permissions for user {UserId}", userId);
            return [];
        }
    }
}
