using Microsoft.AspNetCore.Authorization;

namespace Echelon.Infrastructure.Auth;

/// <summary>Demands one named permission. One requirement per policy.</summary>
/// <param name="permission">The permission the caller must hold; a value from <see cref="Permissions"/>.</param>
public class PermissionRequirement(string permission) : IAuthorizationRequirement
{
    /// <summary>The permission this requirement demands.</summary>
    public string Permission { get; } = permission;
}

/// <summary>
/// Grants a <see cref="PermissionRequirement"/> when the caller carries the matching
/// <c>permission</c> claim.
/// </summary>
/// <remarks>
/// The claims are put there by <see cref="PermissionClaimsTransformation"/> from the stored grants;
/// nothing is looked up here, so authorization stays a claim check on an already-resolved principal.
/// Never calls <c>Fail</c>: a missing claim simply leaves the requirement unmet, so another handler
/// can still satisfy it, whereas failing would veto the policy outright.
/// </remarks>
public class PermissionAuthorizationHandler : AuthorizationHandler<PermissionRequirement>
{
    /// <inheritdoc/>
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, PermissionRequirement requirement)
    {
        var hasClaim = context.User.Claims
            .Any(c => c.Type == "permission" && c.Value == requirement.Permission);

        if (hasClaim)
            context.Succeed(requirement);

        return Task.CompletedTask;
    }
}

/// <summary>
/// Every permission the service defines. These strings are stored in the database and carried in
/// tokens, so renaming one silently revokes it from everybody who holds it.
/// </summary>
public static class Permissions
{
    /// <summary>
    /// Approval decisions: pinning a merge request's readiness or status, and choosing an ungated
    /// environment rule. Separate from <see cref="ConfigEdit"/> because it decides what ships.
    /// </summary>
    public const string ReleasePlanApprove = "release.plan.approve";

    /// <summary>
    /// Administration: connections, repositories, environments, and the permission grants themselves.
    /// A holder can grant this to anyone including themselves, which is why every mutation is audited.
    /// </summary>
    public const string ConfigEdit = "config.edit";

    /// <summary>Read access to plans, tasks, merge requests and rollouts. The baseline every signed-in user holds.</summary>
    public const string ReleasePlanView = "release.plan.view";

    /// <summary>Launching, cancelling, retrying or skipping a rollout. Separate from plan editing.</summary>
    public const string ReleaseExecute = "release.execute";
}
