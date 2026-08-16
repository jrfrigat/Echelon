using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Echelon.Application.DTOs;
using Echelon.Infrastructure.Auth;

namespace Echelon.Web.Controllers;

/// <summary>
/// Turns the caller's principal into the <see cref="ActorRef"/> the audit trail records.
/// </summary>
/// <remarks>
/// One implementation, deliberately. <c>PermissionsController</c> grew its own pair of
/// <c>ActorId</c>/<c>ActorName</c> properties before there was an audit trail; those now delegate
/// here. Two resolutions of "who is calling" would eventually disagree, and the one that disagreed
/// would be the one writing the audit.
/// </remarks>
public static class ActorResolution
{
    /// <summary>Claim carrying the caller's human-readable name on an Entra token.</summary>
    private const string PreferredUsernameClaim = "preferred_username";

    /// <summary>Claim carrying delegated scopes. Present on a user token, absent on an app-only one.</summary>
    private const string ScopeClaim = "scp";

    /// <summary>Entra's explicit identity-type claim, when the tenant emits it.</summary>
    private const string IdentityTypeClaim = "idtyp";

    /// <summary>
    /// Resolves the calling actor.
    /// </summary>
    /// <param name="controller">The controller handling the request.</param>
    /// <returns>
    /// An actor whose <see cref="ActorRef.Kind"/> is <see cref="ActorKinds.Service"/> for an
    /// application identity, <see cref="ActorKinds.User"/> for a person we can name, or
    /// <see cref="ActorKinds.Unidentified"/> for a person we cannot.
    /// </returns>
    /// <remarks>
    /// The service check runs first, and it is not cosmetic. Nothing in this service validates
    /// scopes - there is no <c>RequiredScope</c> and no token-validated hook anywhere - and an app
    /// registration can be granted a permission through the user-override endpoint like any user.
    /// Without this branch a CI pipeline's client-credential token resolves an object id like
    /// anyone else and a deploy to production is attributed to a person who was asleep.
    /// </remarks>
    public static ActorRef ResolveActor(this ControllerBase controller)
    {
        ArgumentNullException.ThrowIfNull(controller);

        var user = controller.User;
        var name = user.FindFirstValue(PreferredUsernameClaim) ?? user.Identity?.Name;

        if (IsApplicationIdentity(user))
            return new ActorRef(ActorKinds.Service, ResolveOid(user), name);

        return UserIdentifier.TryResolve(user, out var oid)
            ? new ActorRef(ActorKinds.User, oid, name)
            // Authenticated, but the token carried no object id we could normalize. Recorded as a
            // person we cannot name rather than as null, so the UI can say which it is.
            : new ActorRef(ActorKinds.Unidentified, null, name);
    }

    /// <summary>
    /// True for a client-credentials (app-only) token: it carries app roles but no delegated scope.
    /// </summary>
    /// <remarks>
    /// Entra emits <c>idtyp=app</c> when the tenant has the optional claim configured, which is the
    /// unambiguous signal; the roles-without-scopes shape is the fallback for tenants that do not.
    /// </remarks>
    private static bool IsApplicationIdentity(ClaimsPrincipal user) =>
        string.Equals(user.FindFirstValue(IdentityTypeClaim), "app", StringComparison.OrdinalIgnoreCase)
        || (user.FindFirst(ClaimTypes.Role) is not null && user.FindFirst(ScopeClaim) is null);

    private static string? ResolveOid(ClaimsPrincipal user) =>
        UserIdentifier.TryResolve(user, out var oid) ? oid : null;
}
