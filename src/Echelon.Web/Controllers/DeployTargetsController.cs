using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using Echelon.Core.Enums;
using Echelon.Application.DTOs;
using Echelon.Infrastructure.Auth;
using Echelon.Infrastructure.Persistence;
using Echelon.Infrastructure.Persistence.Models;
using Echelon.Providers.Abstractions;
using Echelon.Providers.Abstractions.Deploy;
using Echelon.Web.Resources;
using Echelon.Web.Validation;

namespace Echelon.Web.Controllers;

/// <summary>
/// How each repository deploys to each environment - the configuration a rollout resolves its steps'
/// deploy strategy from, and without which a task cannot launch.
/// </summary>
/// <remarks>
/// A target's strategy key is validated against the strategies actually registered, and its settings
/// against that strategy's own schema, exactly as a connection's settings are - so an undeclared
/// setting is refused rather than stored and ignored, and a secret one is encrypted at rest.
/// </remarks>
[ApiController]
[Route("api/deploy-targets")]
[Authorize(Policy = Permissions.ReleasePlanView)]
public class DeployTargetsController(
    AppDbContext db,
    IDeployStrategyFactory strategyFactory,
    TokenProtector protector,
    IStringLocalizer<ApiStrings> localizer) : ControllerBase
{
    /// <summary>Lists deploy targets, optionally for one repository.</summary>
    /// <param name="repositoryId">When given, only that repository's targets.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] Guid? repositoryId, CancellationToken ct)
    {
        var query = db.RepositoryDeployTargets.AsQueryable();
        if (repositoryId is { } id)
            query = query.Where(t => t.RepositoryId == id);

        var rows = await query
            .OrderBy(t => t.Repository.Name).ThenBy(t => t.Environment.Order)
            .Select(t => new
            {
                t.Id,
                t.RepositoryId,
                RepositoryName = t.Repository.Name,
                t.EnvironmentId,
                EnvironmentKey = t.Environment.Key,
                t.DeployStrategyKey,
                t.RedeployPolicy,
                t.DeploySettingsJson,
                t.ReadinessRuleId,
                // Null when this target uses the environment's default rule; the name lets the UI show
                // the override without a second call.
                ReadinessRuleName = t.ReadinessRule != null ? t.ReadinessRule.Name : null
            })
            .ToListAsync(ct);

        // Reshaped after the query: settings are unpacked and secrets withheld in memory.
        var items = rows.Select(t => new DeployTargetDto(
            t.Id,
            t.RepositoryId,
            t.RepositoryName,
            t.EnvironmentId,
            t.EnvironmentKey,
            t.DeployStrategyKey,
            t.RedeployPolicy,
            ReadSettings(t.DeployStrategyKey, t.DeploySettingsJson),
            t.ReadinessRuleId,
            t.ReadinessRuleName));

        return Ok(items);
    }

    /// <summary>The target's non-secret settings, keyed as its strategy declares them.</summary>
    private IReadOnlyDictionary<string, string> ReadSettings(string strategyKey, string? settingsJson) =>
        strategyFactory.AvailableStrategies.Contains(strategyKey)
            ? ProviderSettingsBinder.ReadForDisplay(settingsJson, strategyFactory.GetSettingsSchema(strategyKey))
            : new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Creates a deploy target for a (repository, environment) pair.</summary>
    /// <param name="req">The target to create.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpPost]
    [Authorize(Policy = Permissions.ConfigEdit)]
    public async Task<IActionResult> Create([FromBody] SaveDeployTargetRequest req, CancellationToken ct)
    {
        if (!await db.Repositories.AnyAsync(r => r.Id == req.RepositoryId, ct))
            return BadRequest(new { error = localizer["DeployTarget_UnknownRepository"].Value });

        if (!await db.DeploymentEnvironments.AnyAsync(e => e.Id == req.EnvironmentId, ct))
            return BadRequest(new { error = localizer["DeployTarget_UnknownEnvironment"].Value });

        var strategyKey = ProviderKey.Normalize(req.DeployStrategyKey);
        if (!strategyFactory.AvailableStrategies.Contains(strategyKey))
            return BadRequest(new
            {
                error = localizer[
                    "DeployTarget_UnknownStrategy", req.DeployStrategyKey,
                    string.Join(", ", strategyFactory.AvailableStrategies)].Value
            });

        if (!TryReadPolicy(req.RedeployPolicy, out var policy))
            return BadRequest(new { error = localizer["DeployTarget_UnknownRedeployPolicy", req.RedeployPolicy ?? ""].Value });

        if (req.ReadinessRuleId is { } createRuleId && !await db.ReadinessRules.AnyAsync(r => r.Id == createRuleId, ct))
            return BadRequest(new { error = localizer["Readiness_RuleNotFound"].Value });

        if (await db.RepositoryDeployTargets.AnyAsync(
                t => t.RepositoryId == req.RepositoryId && t.EnvironmentId == req.EnvironmentId, ct))
            return Conflict(new { error = localizer["DeployTarget_AlreadyExists"].Value });

        if (!ProviderSettingsBinder.TryBind(
                req.Settings, strategyFactory.GetSettingsSchema(strategyKey),
                existingJson: null, protector, localizer, out var settingsJson, out var settingsError))
            return BadRequest(new { error = settingsError });

        var target = new RepositoryDeployTarget
        {
            Id = Guid.NewGuid(),
            RepositoryId = req.RepositoryId,
            EnvironmentId = req.EnvironmentId,
            DeployStrategyKey = strategyKey,
            DeploySettingsJson = settingsJson,
            RedeployPolicy = policy,
            ReadinessRuleId = req.ReadinessRuleId
        };

        db.RepositoryDeployTargets.Add(target);
        await db.SaveChangesAsync(ct);
        return CreatedAtAction(nameof(List), new { repositoryId = target.RepositoryId }, new { target.Id });
    }

    /// <summary>Updates a target's strategy, settings, or redeploy policy. The pair it configures is fixed.</summary>
    /// <param name="id">The target id.</param>
    /// <param name="req">The new values.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpPut("{id:guid}")]
    [Authorize(Policy = Permissions.ConfigEdit)]
    public async Task<IActionResult> Update(Guid id, [FromBody] SaveDeployTargetRequest req, CancellationToken ct)
    {
        var target = await db.RepositoryDeployTargets.FindAsync([id], ct);
        if (target is null) return NotFound();

        var strategyKey = ProviderKey.Normalize(req.DeployStrategyKey);
        if (!strategyFactory.AvailableStrategies.Contains(strategyKey))
            return BadRequest(new
            {
                error = localizer[
                    "DeployTarget_UnknownStrategy", req.DeployStrategyKey,
                    string.Join(", ", strategyFactory.AvailableStrategies)].Value
            });

        if (!TryReadPolicy(req.RedeployPolicy, out var policy))
            return BadRequest(new { error = localizer["DeployTarget_UnknownRedeployPolicy", req.RedeployPolicy ?? ""].Value });

        if (req.ReadinessRuleId is { } updateRuleId && !await db.ReadinessRules.AnyAsync(r => r.Id == updateRuleId, ct))
            return BadRequest(new { error = localizer["Readiness_RuleNotFound"].Value });

        // Existing settings passed in so a secret left blank on the form keeps its stored value,
        // the same convention the connection editors use.
        if (!ProviderSettingsBinder.TryBind(
                req.Settings, strategyFactory.GetSettingsSchema(strategyKey),
                target.DeploySettingsJson, protector, localizer, out var settingsJson, out var settingsError))
            return BadRequest(new { error = settingsError });

        target.DeployStrategyKey = strategyKey;
        target.DeploySettingsJson = settingsJson;
        target.RedeployPolicy = policy;
        target.ReadinessRuleId = req.ReadinessRuleId;

        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    /// <summary>Deletes a deploy target.</summary>
    /// <param name="id">The target id.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpDelete("{id:guid}")]
    [Authorize(Policy = Permissions.ConfigEdit)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var target = await db.RepositoryDeployTargets.FindAsync([id], ct);
        if (target is null) return NotFound();

        db.RepositoryDeployTargets.Remove(target);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    /// <summary>
    /// Reads the redeploy policy, defaulting to <see cref="RedeployPolicy.Once"/> when absent.
    /// </summary>
    /// <remarks>
    /// Absent means Once - production's answer, and the safe one - rather than the enum's zero, which
    /// it deliberately does not define. A value that is present and unrecognised is rejected, so a
    /// typo does not quietly become "deploy once".
    /// </remarks>
    private static bool TryReadPolicy(string? value, out RedeployPolicy policy)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            policy = RedeployPolicy.Once;
            return true;
        }

        return Enum.TryParse(value, ignoreCase: true, out policy) && Enum.IsDefined(policy);
    }
}

/// <param name="Settings">
/// Strategy-specific settings, keyed as the chosen strategy's schema declares them. Validated against
/// it; an undeclared key is refused, a secret one is encrypted.
/// </param>
/// <param name="RedeployPolicy">"Once" (default), "Always", or "Inherit". Blank keeps Once.</param>
/// <param name="ReadinessRuleId">
/// The readiness rule this repository uses for this environment, overriding the environment's default;
/// null falls back to that default.
/// </param>
public record SaveDeployTargetRequest(
    [Required] Guid RepositoryId,
    [Required] Guid EnvironmentId,
    [Required, MaxLength(100)] string DeployStrategyKey,
    Dictionary<string, string?>? Settings,
    [MaxLength(20)] string? RedeployPolicy = null,
    Guid? ReadinessRuleId = null);
