using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ReleaseOrchestrator.Infrastructure.Actions;
using ReleaseOrchestrator.Infrastructure.Auth;
using ReleaseOrchestrator.Infrastructure.Persistence;
using ReleaseOrchestrator.Infrastructure.Persistence.Models;
using ReleaseOrchestrator.Providers.Abstractions.Actions;

namespace ReleaseOrchestrator.Web.Controllers;

/// <summary>
/// Action bindings -- "when this event fires, run this action" -- and the discovery of the action
/// types available to bind, with their settings schema so the UI renders a form for a handler it has
/// never heard of.
/// </summary>
/// <remarks>Error messages are not localized yet; localization is folded in during the finalize phase.</remarks>
[ApiController]
[Route("api/action-bindings")]
[Authorize(Policy = Permissions.ReleasePlanView)]
public class ActionBindingsController(AppDbContext db, IActionHandlerFactory factory, TokenProtector protector) : ControllerBase
{
    /// <summary>Lists the action bindings.</summary>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var items = await db.ActionBindings
            .OrderBy(b => b.EventType).ThenBy(b => b.Order).ThenBy(b => b.Id)
            .Select(b => new { b.Id, b.EventType, b.ActionType, b.Scope, b.Order, b.Enabled })
            .ToListAsync(ct);
        return Ok(items);
    }

    /// <summary>The action types available to bind, each with its settings schema.</summary>
    [HttpGet("types")]
    public IActionResult Types() =>
        Ok(factory.AvailableActionTypes.Select(t => new
        {
            ActionType = t,
            Settings = factory.GetSettingsSchema(t)
                .Select(s => new { s.Key, s.Label, s.Description, s.Required, s.Secret })
        }));

    /// <summary>Creates an action binding.</summary>
    /// <param name="req">The binding to create.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpPost]
    [Authorize(Policy = Permissions.ConfigEdit)]
    public async Task<IActionResult> Create([FromBody] SaveActionBindingRequest req, CancellationToken ct)
    {
        if (!factory.AvailableActionTypes.Contains(req.ActionType, StringComparer.OrdinalIgnoreCase))
            return BadRequest(new { error = $"Unknown action type '{req.ActionType}'. Expected one of: {string.Join(", ", factory.AvailableActionTypes)}." });

        var binding = new ActionBinding
        {
            Id = Guid.NewGuid(),
            EventType = req.EventType.Trim(),
            ActionType = req.ActionType.Trim(),
            Scope = string.IsNullOrWhiteSpace(req.Scope) ? null : req.Scope.Trim(),
            // Secret settings (e.g. a Telegram bot token) are encrypted before storage, so they are
            // never at rest in plaintext in SettingsJson -- the same treatment connection tokens get.
            SettingsJson = ActionSecretProtection.ProtectForStorage(
                req.Settings, factory.GetSettingsSchema(req.ActionType), protector),
            Order = req.Order,
            Enabled = req.Enabled
        };
        db.ActionBindings.Add(binding);
        await db.SaveChangesAsync(ct);
        return Ok(new { binding.Id });
    }

    /// <summary>Deletes an action binding.</summary>
    /// <param name="id">The binding id.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpDelete("{id:guid}")]
    [Authorize(Policy = Permissions.ConfigEdit)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var binding = await db.ActionBindings.FirstOrDefaultAsync(b => b.Id == id, ct);
        if (binding is null) return NotFound();

        db.ActionBindings.Remove(binding);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }
}

/// <summary>Request to create an action binding.</summary>
/// <param name="EventType">The event that triggers it, e.g. <c>RolloutSucceeded</c>.</param>
/// <param name="ActionType">The action-handler key, e.g. <c>telegram</c>.</param>
/// <param name="Scope">Optional scope filter, e.g. <c>env:prod</c>.</param>
/// <param name="Settings">Handler settings as a string map.</param>
/// <param name="Order">Order among bindings for the same event.</param>
/// <param name="Enabled">Whether the binding is active.</param>
public record SaveActionBindingRequest(
    [property: Required] string EventType,
    [property: Required] string ActionType,
    string? Scope,
    Dictionary<string, string>? Settings,
    int Order,
    bool Enabled);
