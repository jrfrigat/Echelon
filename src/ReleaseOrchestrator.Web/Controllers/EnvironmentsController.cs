using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ReleaseOrchestrator.Core.Enums;
using ReleaseOrchestrator.Core.Parsing;
using ReleaseOrchestrator.Infrastructure.Auth;
using ReleaseOrchestrator.Infrastructure.Persistence;
using ReleaseOrchestrator.Infrastructure.Persistence.Models;

namespace ReleaseOrchestrator.Web.Controllers;

/// <summary>
/// Deploy environments -- the ordered list a rollout can target ("staging", "prod", ...). The order
/// sets the promotion sequence the progression gate enforces at launch, and each carries the
/// readiness rule that decides which merge requests may deploy into it.
/// </summary>
/// <remarks>Error messages are not localized yet; localization is folded in during the finalize phase.</remarks>
[ApiController]
[Route("api/environments")]
[Authorize(Policy = Permissions.ReleasePlanView)]
public class EnvironmentsController(AppDbContext db, IAuthorizationService authz) : ControllerBase
{
    /// <summary>Lists environments in promotion order, with their readiness rule.</summary>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var rows = await db.DeploymentEnvironments
            .OrderBy(e => e.Order).ThenBy(e => e.Key)
            .Select(e => new { e.Id, e.Key, e.Name, e.Order, e.IsEnabled, e.ReadyRule, e.ReadyLabels })
            .ToListAsync(ct);

        var items = rows.Select(e => new
        {
            e.Id, e.Key, e.Name, e.Order, e.IsEnabled,
            ReadyRule = e.ReadyRule.ToString(),
            ReadyLabels = e.ReadyLabels.Split(',', StringSplitOptions.RemoveEmptyEntries)
        });
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
        if (key.Length == 0) return BadRequest(new { error = "Key is required." });
        if (await db.DeploymentEnvironments.AnyAsync(e => e.Key == key, ct))
            return Conflict(new { error = $"An environment with key '{key}' already exists." });

        if (!TryReadReadiness(req, out var rule, out var readyLabels, out var readinessError))
            return BadRequest(new { error = readinessError });
        if (await RequiresApprovalDenied(rule, ct))
            return Forbid();

        var env = new DeploymentEnvironment
        {
            Id = Guid.NewGuid(),
            Key = key,
            Name = req.Name,
            Order = req.Order,
            IsEnabled = req.IsEnabled,
            ReadyRule = rule,
            ReadyLabels = readyLabels
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

        if (!TryReadReadiness(req, out var rule, out var readyLabels, out var readinessError))
            return BadRequest(new { error = readinessError });
        // Only a change TO NoGate needs approval -- leaving an already-ungated environment as it was
        // is not a new decision, so a routine rename does not demand the approver.
        if (rule == ReadyRule.NoGate && env.ReadyRule != ReadyRule.NoGate && await RequiresApprovalDenied(rule, ct))
            return Forbid();

        env.Name = req.Name;
        env.Order = req.Order;
        env.IsEnabled = req.IsEnabled;
        env.ReadyRule = rule;
        env.ReadyLabels = readyLabels;
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
    /// Reads the readiness rule and labels from the request, defaulting an absent rule to
    /// <see cref="ReadyRule.NoGate"/> and canonicalising the labels.
    /// </summary>
    private static bool TryReadReadiness(
        SaveEnvironmentRequest req, out ReadyRule rule, out string readyLabels, out string error)
    {
        readyLabels = LabelSet.Canonical(req.ReadyLabels);
        error = string.Empty;

        // Absent means NoGate -- an environment created without a stated rule is ungated, the
        // behaviour that existed before there was a gate. A present but unrecognised value is
        // rejected rather than defaulted, so a typo does not quietly ungate a production environment.
        if (string.IsNullOrWhiteSpace(req.ReadyRule))
        {
            rule = ReadyRule.NoGate;
            return true;
        }

        if (!Enum.TryParse(req.ReadyRule, ignoreCase: true, out rule) || !Enum.IsDefined(rule))
        {
            error = $"Unknown readiness rule '{req.ReadyRule}'. Valid: NoGate, AnyOf, AllOf.";
            return false;
        }

        return true;
    }

    /// <summary>
    /// True when the rule needs approval permission the caller does not have.
    /// </summary>
    /// <remarks>
    /// Ungating an environment (<see cref="ReadyRule.NoGate"/>) is an approval decision, not a
    /// configuration one -- it removes the check between unreviewed code and that environment -- so it
    /// is gated on <see cref="Permissions.ReleasePlanApprove"/> even though the rest of the edit is
    /// ordinary config. Tightening a gate (AnyOf/AllOf) or setting labels stays at config-edit.
    /// </remarks>
    private async Task<bool> RequiresApprovalDenied(ReadyRule rule, CancellationToken ct)
    {
        if (rule != ReadyRule.NoGate) return false;
        var result = await authz.AuthorizeAsync(User, Permissions.ReleasePlanApprove);
        return !result.Succeeded;
    }
}

/// <summary>Request to create or update a deploy environment.</summary>
/// <param name="Key">Stable key (e.g. "staging"). Required on create; ignored on update.</param>
/// <param name="Name">Operator-facing name.</param>
/// <param name="Order">Promotion order; lower deploys first.</param>
/// <param name="IsEnabled">Whether rollouts may target it.</param>
/// <param name="ReadyRule">"NoGate" (default), "AnyOf", or "AllOf". Setting NoGate needs approval permission.</param>
/// <param name="ReadyLabels">The labels the rule is evaluated against; ignored when the rule is NoGate.</param>
public record SaveEnvironmentRequest(
    [property: Required] string Key,
    [property: Required] string Name,
    int Order,
    bool IsEnabled,
    string? ReadyRule = null,
    IReadOnlyList<string>? ReadyLabels = null);
