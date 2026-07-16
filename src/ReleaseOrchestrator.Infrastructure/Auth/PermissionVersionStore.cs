using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using ReleaseOrchestrator.Infrastructure.Persistence;

namespace ReleaseOrchestrator.Infrastructure.Auth;

/// <summary>
/// Supplies the stamp that prefixes every permission cache key, so that changing a rule retires
/// every entry derived from the old rules at once.
/// </summary>
/// <remarks>
/// The stamp is a hash of the stored rules, not a counter. A counter held only in Redis rolls back
/// to "0" the moment its key is evicted (maxmemory-policy allkeys-lru drops it like any other key,
/// its 365-day TTL notwithstanding). Surviving "perm:0:*" entries then became authoritative again
/// and handed back permissions that had just been revoked. A content hash recomputes to the same
/// value after an eviction and to a new one after a change, so losing the key costs three small
/// reads and never correctness — the database, not Redis, is the source of truth.
/// </remarks>
public static class PermissionVersionStore
{
    public const string VersionCacheKey = "perm:version";

    // Deliberately short. The stamp is shared by every user, so recomputing it is cheap, whereas a
    // long-lived copy would both hide rules edited straight in the database and widen the window in
    // which a reader that computed the stamp just before a write stores it back after the writer
    // dropped it. Per-user entries still live their full TTL: while the rules hold still the stamp
    // recomputes to the same value, so their keys stay valid.
    private static readonly DistributedCacheEntryOptions CacheOptions = new()
    {
        AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(30)
    };

    public static async Task<string> GetAsync(AppDbContext db, IDistributedCache cache, CancellationToken ct = default)
    {
        var cached = await cache.GetStringAsync(VersionCacheKey, ct);
        if (!string.IsNullOrEmpty(cached)) return cached;

        var version = await ComputeAsync(db, ct);
        await cache.SetStringAsync(VersionCacheKey, version, CacheOptions, ct);
        return version;
    }

    /// <summary>Drops the stamp so the next reader derives it from the rules as they now stand.</summary>
    public static Task InvalidateAsync(IDistributedCache cache, CancellationToken ct = default) =>
        cache.RemoveAsync(VersionCacheKey, ct);

    private static async Task<string> ComputeAsync(AppDbContext db, CancellationToken ct)
    {
        // Claim names belong in the hash too: cached entries hold names, so renaming a claim
        // changes what every mapping to it grants without touching a single mapping row.
        var claims = await db.PermissionClaims
            .Select(c => new { c.Id, c.Name }).ToListAsync(ct);
        var groupMappings = await db.GroupPermissionMappings
            .Select(m => new { m.AdGroupSid, m.PermissionClaimId }).ToListAsync(ct);
        var userOverrides = await db.UserPermissionOverrides
            .Select(o => new { o.UserId, o.PermissionClaimId }).ToListAsync(ct);

        return StableHash.Of(
        [
            .. claims.Select(c => $"c:{c.Id}:{c.Name}"),
            .. groupMappings.Select(m => $"g:{m.AdGroupSid}:{m.PermissionClaimId}"),
            .. userOverrides.Select(o => $"u:{o.UserId}:{o.PermissionClaimId}")
        ]);
    }
}

/// <summary>
/// Order-insensitive hash for cache keys. <c>GetHashCode</c> cannot stand in: it is randomised per
/// process, so two replicas would disagree about the key for identical input.
/// </summary>
public static class StableHash
{
    public static string Of(IEnumerable<string> values)
    {
        var canonical = string.Join('\n', values.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))[..32];
    }
}
