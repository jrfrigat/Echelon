using System.Security.Claims;

namespace ReleaseOrchestrator.Infrastructure.Auth;

/// <summary>
/// Resolves the one identifier permissions are keyed on: the Entra ID object id ("oid").
/// </summary>
/// <remarks>
/// Permission lookup used to prefer <see cref="ClaimTypes.NameIdentifier"/>, which for Entra ID
/// carries "sub" — a pairwise value, unique per user *and application*, that appears nowhere in a
/// user's profile. An administrator granting an override reads the object id off the portal, so
/// the row they created never matched the token and the grant silently did nothing. "oid" is the
/// same value in the token and in the portal, so both ends now agree.
/// </remarks>
public static class UserIdentifier
{
    public const string ObjectIdClaimType = "oid";

    /// <summary>Carries the object id instead of <see cref="ObjectIdClaimType"/> when inbound claim mapping is on.</summary>
    public const string ObjectIdClaimUri = "http://schemas.microsoft.com/identity/claims/objectidentifier";

    public static bool TryResolve(ClaimsPrincipal principal, out string userId) =>
        TryNormalize(
            principal.FindFirstValue(ObjectIdClaimType) ?? principal.FindFirstValue(ObjectIdClaimUri),
            out userId);

    /// <summary>
    /// Object ids are GUIDs. Parsing rejects a UPN or an email typed into the admin UI, and the
    /// canonical form makes stored overrides match tokens regardless of casing or braces.
    /// </summary>
    public static bool TryNormalize(string? rawUserId, out string userId)
    {
        if (Guid.TryParse(rawUserId, out var objectId) && objectId != Guid.Empty)
        {
            userId = objectId.ToString("D");
            return true;
        }

        userId = string.Empty;
        return false;
    }
}
