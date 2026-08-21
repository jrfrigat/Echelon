using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using Echelon.Core.Enums;
using Echelon.Core.Parsing;
using Echelon.Infrastructure.Auth;
using Echelon.Infrastructure.Persistence;
using Echelon.Web.Resources;
using Echelon.Infrastructure.Persistence.Models;

namespace Echelon.Web.Controllers;

/// <summary>
/// Named readiness rules: the reusable "prod-strict", "test-loose" an environment or a repository
/// points at to decide which merge requests may deploy.
/// </summary>
/// <remarks>
/// A rule is created once here and assigned elsewhere - an environment's default rule
/// (EnvironmentsController) or a repository's override for one environment (DeployTargetsController).
/// A rule matches a merge request's signals (<c>label:*</c>, <c>mr-status:*</c>, <c>pipeline:*</c>);
/// see <see cref="ReadinessSignals"/>. Error messages are not localized yet, matching the sibling
/// environment controller. Mutations are ordinary config; only removing a gate is an approval decision,
/// and that lives where a rule is unassigned, not here.
/// </remarks>
[ApiController]
[Route("api/readiness-rules")]
[Authorize(Policy = Permissions.ReleasePlanView)]
public class ReadinessRulesController(
    AppDbContext db,
    IStringLocalizer<ApiStrings> localizer) : ControllerBase
{
    /// <summary>Lists the readiness rules.</summary>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var items = await db.ReadinessRules
            .OrderBy(r => r.Name)
            .Select(r => new
            {
                r.Id, r.Name,
                Mode = r.Mode.ToString(),
                RequiredSignals = r.RequiredSignals.Split(',', StringSplitOptions.RemoveEmptyEntries)
            })
            .ToListAsync(ct);

        return Ok(items);
    }

    /// <summary>Creates a readiness rule.</summary>
    /// <param name="req">The rule.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpPost]
    [Authorize(Policy = Permissions.ConfigEdit)]
    public async Task<IActionResult> Create([FromBody] SaveReadinessRuleRequest req, CancellationToken ct)
    {
        if (!TryRead(req, localizer, out var name, out var mode, out var signals, out var error))
            return BadRequest(new { error });
        if (await db.ReadinessRules.AnyAsync(r => r.Name == name, ct))
            return Conflict(new { error = $"A readiness rule named '{name}' already exists." });

        var rule = new ReadinessRule { Id = Guid.NewGuid(), Name = name, Mode = mode, RequiredSignals = signals };
        db.ReadinessRules.Add(rule);
        await db.SaveChangesAsync(ct);
        return Ok(new { rule.Id });
    }

    /// <summary>Updates a readiness rule.</summary>
    /// <param name="id">The rule id.</param>
    /// <param name="req">The new values.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpPut("{id:guid}")]
    [Authorize(Policy = Permissions.ConfigEdit)]
    public async Task<IActionResult> Update(Guid id, [FromBody] SaveReadinessRuleRequest req, CancellationToken ct)
    {
        var rule = await db.ReadinessRules.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (rule is null) return NotFound();

        if (!TryRead(req, localizer, out var name, out var mode, out var signals, out var error))
            return BadRequest(new { error });
        if (await db.ReadinessRules.AnyAsync(r => r.Name == name && r.Id != id, ct))
            return Conflict(new { error = $"A readiness rule named '{name}' already exists." });

        rule.Name = name;
        rule.Mode = mode;
        rule.RequiredSignals = signals;
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    /// <summary>Deletes a readiness rule that nothing uses.</summary>
    /// <param name="id">The rule id.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpDelete("{id:guid}")]
    [Authorize(Policy = Permissions.ConfigEdit)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var rule = await db.ReadinessRules.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (rule is null) return NotFound();

        // The environment and deploy-target foreign keys are Restrict, so a delete while a rule is
        // assigned would FK-violate and surface as a 500. Report it cleanly and name where it is used,
        // as the sibling controllers do for their own in-use configuration.
        var envCount = await db.DeploymentEnvironments.CountAsync(e => e.ReadinessRuleId == id, ct);
        var targetCount = await db.RepositoryDeployTargets.CountAsync(t => t.ReadinessRuleId == id, ct);
        if (envCount + targetCount > 0)
            return Conflict(new
            {
                error = $"Rule '{rule.Name}' is still assigned to {envCount} environment(s) and "
                        + $"{targetCount} repository target(s); unassign it first."
            });

        db.ReadinessRules.Remove(rule);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    /// <summary>
    /// Validates and normalizes a submitted rule: a non-blank name, a real gating mode, and a
    /// non-empty canonical signal set.
    /// </summary>
    /// <remarks>
    /// Takes the localizer rather than being static: these three sentences are shown to whoever is
    /// filling the form in, and the rest of the API answers that person in their own language.
    /// </remarks>
    private static bool TryRead(
        SaveReadinessRuleRequest req, IStringLocalizer<ApiStrings> localizer,
        out string name, out ReadyRule mode, out string signals, out string error)
    {
        name = (req.Name ?? string.Empty).Trim();
        signals = LabelSet.Canonical(req.RequiredSignals);
        mode = ReadyRule.AllOf;
        error = string.Empty;

        if (name.Length == 0)
        {
            error = localizer["Common_NameRequired"].Value;
            return false;
        }

        // A named rule gates: NoGate is the absence of a rule, not a rule you can name, so it is
        // rejected here rather than stored as a rule that admits everything.
        if (!Enum.TryParse(req.Mode, ignoreCase: true, out mode) || (mode != ReadyRule.AnyOf && mode != ReadyRule.AllOf))
        {
            error = localizer["RR_ModeUnknown", req.Mode ?? string.Empty].Value;
            return false;
        }

        if (signals.Length == 0)
        {
            error = localizer["RR_SignalRequired"].Value;
            return false;
        }

        return true;
    }
}

/// <summary>Request to create or update a readiness rule.</summary>
/// <param name="Name">Operator-facing name; unique.</param>
/// <param name="Mode">"AnyOf" or "AllOf" - whether any one required signal suffices, or all are needed.</param>
/// <param name="RequiredSignals">
/// The signals a merge request must carry, as <c>kind:value</c> tokens (<c>label:ready-for-prod</c>,
/// <c>mr-status:merged</c>, <c>pipeline:success</c>). Canonicalized on save; at least one is required.
/// </param>
public record SaveReadinessRuleRequest(
    [Required] string Name,
    [Required] string Mode,
    IReadOnlyList<string>? RequiredSignals = null);
