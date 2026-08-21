using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using Echelon.Application.DTOs;
using Echelon.Infrastructure.Auth;
using Echelon.Infrastructure.Persistence;
using Echelon.Infrastructure.Persistence.Models;
using Echelon.Web.Resources;

namespace Echelon.Web.Controllers;

/// <summary>
/// Deploy environments -- the ordered list a rollout can target ("staging", "prod", ...). The order
/// sets the promotion sequence the progression gate enforces at launch, and each carries the
/// readiness rule that decides which merge requests may deploy into it.
/// </summary>
/// <remarks>Error messages are not localized yet; localization is folded in during the finalize phase.</remarks>
[ApiController]
[Route("api/environments")]
[Authorize(Policy = Permissions.ReleasePlanView)]
public class EnvironmentsController(
    AppDbContext db,
    IAuthorizationService authz,
    IStringLocalizer<ApiStrings> localizer) : ControllerBase
{
    /// <summary>Lists environments in promotion order, with their readiness rule.</summary>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var items = await db.DeploymentEnvironments
            .OrderBy(e => e.Order).ThenBy(e => e.Key)
            .Select(e => new EnvironmentDto(
                e.Id,
                e.Key,
                e.Name,
                e.Order,
                e.IsEnabled,
                e.ReadinessRuleId,
                // Null when ungated; the name lets the UI show which rule without a second call.
                e.ReadinessRule != null ? e.ReadinessRule.Name : null))
            .ToListAsync(ct);

        return Ok(items);
    }

    /// <summary>Creates an environment.</summary>
    /// <param name="req">The environment to create.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpPost]
    [Authorize(Policy = Permissions.ConfigEdit)]
    public async Task<IActionResult> Create([FromBody] SaveEnvironmentRequest req, CancellationToken ct)
    {
        var key = req.Key.Trim();
        if (key.Length == 0) return BadRequest(new { error = localizer["Env_KeyRequired"].Value });
        if (await db.DeploymentEnvironments.AnyAsync(e => e.Key == key, ct))
            return Conflict(new { error = $"An environment with key '{key}' already exists." });

        if (req.ReadinessRuleId is { } ruleId && !await db.ReadinessRules.AnyAsync(r => r.Id == ruleId, ct))
            return BadRequest(new { error = localizer["Readiness_RuleNotFound"].Value });
        // Creating an ungated environment (no rule) is an approval decision, not a config one.
        if (req.ReadinessRuleId is null && await ApprovalDenied(ct))
            return Forbid();

        var env = new DeploymentEnvironment
        {
            Id = Guid.NewGuid(),
            Key = key,
            Name = req.Name,
            Order = req.Order,
            IsEnabled = req.IsEnabled,
            ReadinessRuleId = req.ReadinessRuleId
        };
        db.DeploymentEnvironments.Add(env);
        await db.SaveChangesAsync(ct);
        return Ok(new { env.Id });
    }

    /// <summary>Updates an environment's name, order, enabled flag, and readiness rule.</summary>
    /// <param name="id">The environment id.</param>
    /// <param name="req">The new values.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpPut("{id:guid}")]
    [Authorize(Policy = Permissions.ConfigEdit)]
    public async Task<IActionResult> Update(Guid id, [FromBody] SaveEnvironmentRequest req, CancellationToken ct)
    {
        var env = await db.DeploymentEnvironments.FirstOrDefaultAsync(e => e.Id == id, ct);
        if (env is null) return NotFound();

        if (req.ReadinessRuleId is { } ruleId && !await db.ReadinessRules.AnyAsync(r => r.Id == ruleId, ct))
            return BadRequest(new { error = localizer["Readiness_RuleNotFound"].Value });
        // Only removing the gate (to no rule) on an environment that had one is a new approval
        // decision; a routine rename that leaves an already-ungated environment ungated is not.
        if (req.ReadinessRuleId is null && env.ReadinessRuleId is not null && await ApprovalDenied(ct))
            return Forbid();

        env.Name = req.Name;
        env.Order = req.Order;
        env.IsEnabled = req.IsEnabled;
        env.ReadinessRuleId = req.ReadinessRuleId;
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    /// <summary>Deletes an environment.</summary>
    /// <param name="id">The environment id.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpDelete("{id:guid}")]
    [Authorize(Policy = Permissions.ConfigEdit)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var env = await db.DeploymentEnvironments.FirstOrDefaultAsync(e => e.Id == id, ct);
        if (env is null) return NotFound();

        // Rollout, MrDeploymentState and MrDeployClaim reference the environment with Restrict, so a
        // delete while any exist would FK-violate and surface as an opaque 500. Report it as a clean
        // Conflict instead, as the sibling connection/repository controllers do -- disabling the
        // environment (IsEnabled = false) is the way to retire it without losing its history.
        if (await db.Rollouts.AnyAsync(r => r.EnvironmentId == id, ct)
            || await db.MrDeploymentStates.AnyAsync(s => s.EnvironmentId == id, ct)
            || await db.MrDeployClaims.AnyAsync(c => c.EnvironmentId == id, ct))
            return Conflict(new { error = $"Environment '{env.Key}' has rollout or deployment history and cannot be deleted. Disable it instead." });

        // Deploy targets and readiness pins also reference the environment with Restrict. Unlike
        // history, these are pure configuration, so the fix is to remove them first rather than to
        // disable the environment -- say so, and name the count, rather than surfacing an opaque 500.
        var targetCount = await db.RepositoryDeployTargets.CountAsync(t => t.EnvironmentId == id, ct);
        if (targetCount > 0)
            return Conflict(new { error = $"Environment '{env.Key}' still has {targetCount} repository deploy target(s); remove them first." });
        var pinCount = await db.MergeRequestReadinessPins.CountAsync(p => p.EnvironmentId == id, ct);
        if (pinCount > 0)
            return Conflict(new { error = $"Environment '{env.Key}' still has {pinCount} readiness pin(s); remove them first." });

        db.DeploymentEnvironments.Remove(env);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    /// <summary>
    /// True when the caller lacks the approval permission that ungating an environment requires.
    /// </summary>
    /// <remarks>
    /// Leaving an environment without a readiness rule removes the check between unreviewed code and
    /// that environment, so it is an approval decision gated on
    /// <see cref="Permissions.ReleasePlanApprove"/> rather than ordinary config editing. Assigning a
    /// rule (which gates) stays at config-edit.
    /// </remarks>
    private async Task<bool> ApprovalDenied(CancellationToken ct)
    {
        _ = ct;
        var result = await authz.AuthorizeAsync(User, Permissions.ReleasePlanApprove);
        return !result.Succeeded;
    }
}

/// <summary>Request to create or update a deploy environment.</summary>
/// <param name="Key">Stable key (e.g. "staging"). Required on create; ignored on update.</param>
/// <param name="Name">Operator-facing name.</param>
/// <param name="Order">Promotion order; lower deploys first.</param>
/// <param name="IsEnabled">Whether rollouts may target it.</param>
/// <param name="ReadinessRuleId">
/// The named readiness rule this environment applies, or null for no gate. Setting it to null (or
/// creating without one) needs approval permission, because it removes the gate.
/// </param>
public record SaveEnvironmentRequest(
    [Required] string Key,
    [Required] string Name,
    int Order,
    bool IsEnabled,
    Guid? ReadinessRuleId = null);
