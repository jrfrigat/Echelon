using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ReleaseOrchestrator.Infrastructure.Auth;
using ReleaseOrchestrator.Infrastructure.Persistence;
using ReleaseOrchestrator.Infrastructure.Persistence.Models;

namespace ReleaseOrchestrator.Web.Controllers;

/// <summary>
/// Deploy environments -- the ordered list a rollout can target ("staging", "prod", ...). The
/// order sets the promotion sequence the progression gate enforces at launch.
/// </summary>
/// <remarks>Error messages are not localized yet; localization is folded in during the finalize phase.</remarks>
[ApiController]
[Route("api/environments")]
[Authorize(Policy = Permissions.ReleasePlanView)]
public class EnvironmentsController(AppDbContext db) : ControllerBase
{
    /// <summary>Lists environments in promotion order.</summary>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var items = await db.DeploymentEnvironments
            .OrderBy(e => e.Order).ThenBy(e => e.Key)
            .Select(e => new { e.Id, e.Key, e.Name, e.Order, e.IsEnabled })
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
        if (key.Length == 0) return BadRequest(new { error = "Key is required." });
        if (await db.DeploymentEnvironments.AnyAsync(e => e.Key == key, ct))
            return Conflict(new { error = $"An environment with key '{key}' already exists." });

        var env = new DeploymentEnvironment
        {
            Id = Guid.NewGuid(),
            Key = key,
            Name = req.Name,
            Order = req.Order,
            IsEnabled = req.IsEnabled
        };
        db.DeploymentEnvironments.Add(env);
        await db.SaveChangesAsync(ct);
        return Ok(new { env.Id });
    }

    /// <summary>Updates an environment's name, order, or enabled flag.</summary>
    /// <param name="id">The environment id.</param>
    /// <param name="req">The new values.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpPut("{id:guid}")]
    [Authorize(Policy = Permissions.ConfigEdit)]
    public async Task<IActionResult> Update(Guid id, [FromBody] SaveEnvironmentRequest req, CancellationToken ct)
    {
        var env = await db.DeploymentEnvironments.FirstOrDefaultAsync(e => e.Id == id, ct);
        if (env is null) return NotFound();

        env.Name = req.Name;
        env.Order = req.Order;
        env.IsEnabled = req.IsEnabled;
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

        // Deploy targets also reference the environment with Restrict. Unlike history, these are
        // pure configuration, so the fix is to remove them first rather than to disable the
        // environment -- say so, and name the count, rather than surfacing an opaque FK 500.
        var targetCount = await db.RepositoryDeployTargets.CountAsync(t => t.EnvironmentId == id, ct);
        if (targetCount > 0)
            return Conflict(new { error = $"Environment '{env.Key}' still has {targetCount} repository deploy target(s); remove them first." });

        db.DeploymentEnvironments.Remove(env);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }
}

/// <summary>Request to create or update a deploy environment.</summary>
/// <param name="Key">Stable key (e.g. "staging"). Required on create; ignored on update.</param>
/// <param name="Name">Operator-facing name.</param>
/// <param name="Order">Promotion order; lower deploys first.</param>
/// <param name="IsEnabled">Whether rollouts may target it.</param>
public record SaveEnvironmentRequest(
    [property: Required] string Key,
    [property: Required] string Name,
    int Order,
    bool IsEnabled);
