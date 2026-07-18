using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using ReleaseOrchestrator.Infrastructure.Persistence;

namespace ReleaseOrchestrator.Infrastructure.Auth;

public class PermissionClaimsTransformation(
    AppDbContext db,
    IDistributedCache cache,
    IOptions<PermissionBootstrapOptions> bootstrap,
    ILogger<PermissionClaimsTransformation> logger) : IClaimsTransformation
{
    public const string PermissionClaimType = "permission";
    public const string GroupClaimType = "groups";

    /// <summary>Marks the identity this transformation attaches, so a second run can recognise its own work.</summary>
    public const string PermissionIdentityAuthenticationType = "PermissionClaims";

    private static readonly DistributedCacheEntryOptions CacheOptions = new()
    {
        AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5)
    };

    public async Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        if (principal.Identity?.IsAuthenticated != true) return principal;

        // ASP.NET Core runs claims transformation once per authentication, but that can happen
        // several times within one request; unguarded, every run appended another copy of the
        // permission claims to the same principal.
        if (principal.Identities.Any(i => i.AuthenticationType == PermissionIdentityAuthenticationType))
            return principal;

        var identity = new ClaimsIdentity(PermissionIdentityAuthenticationType);
        identity.AddClaims((await ResolvePermissionsAsync(principal)).Select(p => new Claim(PermissionClaimType, p)));

        // Clone rather than mutate: the incoming principal belongs to the authentication handler and
        // may be reused, so claims added to it leak past this request.
        var result = principal.Clone();
        result.AddIdentity(identity);
        return result;
    }

    private async Task<IReadOnlyList<string>> ResolvePermissionsAsync(ClaimsPrincipal principal)
    {
        // Fail closed. An unresolvable subject used to fall back to "", collapsing the key to
        // "perm:{version}:" — one entry shared by everybody in that state. The entry holds the
        // permissions computed from the first such caller's groups, so an admin-group member
        // populated it and every subject without an object id inherited their rights until it aged out.
        if (!UserIdentifier.TryResolve(principal, out var userId))
        {
            logger.LogWarning("Authenticated principal carries no usable '{ClaimType}' claim; granting no permissions.",
                UserIdentifier.ObjectIdClaimType);
            return [];
        }

        // Bootstrap escape hatch. Permissions live in the database and are granted through
        // config.edit — which nobody can hold on a fresh install, because granting it needs
        // config.edit. Without this the deployment is unusable and the only way in is hand-
        // editing the database. It is not a privilege boundary: whoever sets this variable
        // already owns the connection string.
        if (IsBootstrapAdmin(userId))
        {
            logger.LogWarning(
                "User {UserId} is granted every permission by Authorization:BootstrapAdminObjectIds. "
                + "This is a bootstrap-only setting — grant the permissions properly and remove it.",
                userId);
            return [Permissions.ConfigEdit, Permissions.ReleasePlanApprove, Permissions.ReleasePlanView, Permissions.ReleaseExecute];
        }

        var groupSids = principal.FindAll(GroupClaimType).Select(c => c.Value).ToList();

        try
        {
            var version = await PermissionVersionStore.GetAsync(db, cache);

            // Groups are an input to the result, so they belong in the key. Keyed on the user alone,
            // a result computed from one set of groups was replayed for the same user presenting a
            // different set — a token issued without the groups claim, a groups overage, or plain
            // membership churn — for the rest of the TTL.
            var cacheKey = $"perm:{version}:{userId}:{StableHash.Of(groupSids)}";

            var cached = await cache.GetStringAsync(cacheKey);
            if (cached is not null)
                return JsonSerializer.Deserialize<List<string>>(cached) ?? [];

            var permissions = new HashSet<string>(StringComparer.Ordinal);

            var groupPermissions = await db.GroupPermissionMappings
                .Where(m => groupSids.Contains(m.AdGroupSid))
                .Select(m => m.PermissionClaim.Name)
                .ToListAsync();

            foreach (var p in groupPermissions) permissions.Add(p);

            var userPermissions = await db.UserPermissionOverrides
                .Where(o => o.UserId == userId)
                .Select(o => o.PermissionClaim.Name)
                .ToListAsync();

            foreach (var p in userPermissions) permissions.Add(p);

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

    /// <summary>Normalised the same way stored overrides are, so both ends compare identically.</summary>
    private bool IsBootstrapAdmin(string userId) =>
        bootstrap.Value.BootstrapAdminObjectIds
            .Any(id => UserIdentifier.TryNormalize(id, out var normalized)
                       && string.Equals(normalized, userId, StringComparison.Ordinal));
}
