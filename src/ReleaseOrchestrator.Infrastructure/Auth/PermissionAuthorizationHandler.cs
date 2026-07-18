using Microsoft.AspNetCore.Authorization;

namespace ReleaseOrchestrator.Infrastructure.Auth;

public class PermissionRequirement(string permission) : IAuthorizationRequirement
{
    public string Permission { get; } = permission;
}

public class PermissionAuthorizationHandler : AuthorizationHandler<PermissionRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, PermissionRequirement requirement)
    {
        var hasClaim = context.User.Claims
            .Any(c => c.Type == "permission" && c.Value == requirement.Permission);

        if (hasClaim)
            context.Succeed(requirement);

        return Task.CompletedTask;
    }
}

public static class Permissions
{
    public const string ReleasePlanApprove = "release.plan.approve";
    public const string ConfigEdit = "config.edit";
    public const string ReleasePlanView = "release.plan.view";

    /// <summary>Launching, cancelling, retrying or skipping a rollout. Separate from plan editing.</summary>
    public const string ReleaseExecute = "release.execute";
}
