using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Localization;
using ReleaseOrchestrator.Infrastructure.Persistence.Models;
using ReleaseOrchestrator.Infrastructure.Auth;
using ReleaseOrchestrator.Infrastructure.Ingestion;
using ReleaseOrchestrator.Infrastructure.Persistence;
using ReleaseOrchestrator.Providers.Abstractions;
using ReleaseOrchestrator.Providers.Abstractions.Vcs;
using ReleaseOrchestrator.Web.Resources;
using ReleaseOrchestrator.Web.Validation;

namespace ReleaseOrchestrator.Web.Controllers;

[ApiController]
[Route("api/vcs-connections")]
[Authorize(Policy = Permissions.ReleasePlanView)]
public class VcsConnectionsController(
    AppDbContext db,
    TokenProtector protector,
    IVcsProviderFactory providerFactory,
    IConfiguration config,
    IStringLocalizer<ApiStrings> localizer) : ControllerBase
{
    private string[] AllowedApiHosts => config.GetSection("Security:AllowedApiHosts").Get<string[]>() ?? [];

    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] int page = 1, [FromQuery] int pageSize = Paging.DefaultPageSize, CancellationToken ct = default)
    {
        var paging = Paging.From(page, pageSize);
        var total = await db.VcsConnections.CountAsync(ct);

        // OrderBy is required for paging to be stable: without it SQL Server may return rows
        // in any order, so entries can repeat or vanish between pages.
        //
        // The wire keeps saying "vcsType" while the column is now ProviderType. The name was
        // never the problem — an enum in the domain was — and renaming the field would break
        // every client for no gain.
        // Reshaped in memory after paging: the settings bag is JSON in a column, so it cannot be
        // unpacked in SQL.
        var rows = await db.VcsConnections
            .OrderBy(c => c.Name).ThenBy(c => c.Id)
            .Select(c => new
            {
                c.Id, c.Name, c.ProviderType, c.ApiUrl, c.ProviderSettingsJson
            })
            .Skip(paging.Skip).Take(paging.PageSize)
            .ToListAsync(ct);

        var items = rows
            .Select(c => new
            {
                c.Id, c.Name, VcsType = c.ProviderType, c.ApiUrl,
                Settings = ReadSettings(c.ProviderType, c.ProviderSettingsJson)
            })
            .ToList();

        return Ok(new { Total = total, Page = paging.Page, PageSize = paging.PageSize, Items = items });
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var c = await db.VcsConnections
            .Where(x => x.Id == id)
            .Select(x => new
            {
                x.Id, x.Name, x.ProviderType, x.ApiUrl, x.ProviderSettingsJson
            })
            .FirstOrDefaultAsync(ct);

        return c is null
            ? NotFound()
            : Ok(new
            {
                c.Id, c.Name, VcsType = c.ProviderType, c.ApiUrl,
                Settings = ReadSettings(c.ProviderType, c.ProviderSettingsJson)
            });
    }

    /// <summary>
    /// The connection's non-secret settings, keyed as its provider declared them.
    /// </summary>
    /// <remarks>
    /// A connection whose provider is no longer registered still lists, with no settings: the schema
    /// needed to interpret the bag is gone, but hiding the row would hide the only thing an operator
    /// could act on, which is that it points at a provider this build does not have.
    /// </remarks>
    private IReadOnlyDictionary<string, string> ReadSettings(string providerType, string? settingsJson) =>
        providerFactory.AvailableProviders.Contains(providerType)
            ? ProviderSettingsBinder.ReadForDisplay(settingsJson, providerFactory.GetSettingsSchema(providerType))
            : new Dictionary<string, string>(StringComparer.Ordinal);

    [HttpPost]
    [Authorize(Policy = Permissions.ConfigEdit)]
    public async Task<IActionResult> Create([FromBody] CreateVcsConnectionRequest req, CancellationToken ct)
    {
        // Validated against the adapters that are actually registered, not against an enum. The
        // set of providers is a property of the composition root, and this is the last point at
        // which an operator's typo can be rejected with the list of what would have worked —
        // after this it is a stored row that fails on first use.
        var providerType = ProviderKey.Normalize(req.VcsType);
        if (!providerFactory.AvailableProviders.Contains(providerType))
            return BadRequest(new
            {
                error = localizer["Vcs_UnknownType", req.VcsType, string.Join(", ", providerFactory.AvailableProviders)].Value
            });

        if (!ApiUrlValidator.TryValidate(req.ApiUrl, AllowedApiHosts, localizer, out var urlError))
            return BadRequest(new { error = urlError });

        if (await db.VcsConnections.AnyAsync(c => c.Name == req.Name, ct))
            return Conflict(new { error = localizer["Vcs_NameTaken", req.Name].Value });

        if (!ProviderSettingsBinder.TryBind(
                req.Settings, providerFactory.GetSettingsSchema(providerType),
                existingJson: null, protector, localizer, out var settingsJson, out var settingsError))
            return BadRequest(new { error = settingsError });

        var entity = new VcsConnection
        {
            Id = Guid.NewGuid(),
            Name = req.Name,
            ProviderType = providerType,
            ApiUrl = req.ApiUrl,
            ProviderSettingsJson = settingsJson,
            EncryptedAccessToken = protector.Protect(req.AccessToken)
        };

        db.VcsConnections.Add(entity);
        await db.SaveChangesAsync(ct);
        return CreatedAtAction(nameof(Get), new { id = entity.Id }, new { entity.Id, entity.Name });
    }

    [HttpPut("{id:guid}")]
    [Authorize(Policy = Permissions.ConfigEdit)]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateVcsConnectionRequest req, CancellationToken ct)
    {
        if (!ApiUrlValidator.TryValidate(req.ApiUrl, AllowedApiHosts, localizer, out var urlError))
            return BadRequest(new { error = urlError });

        var entity = await db.VcsConnections.FindAsync([id], ct);
        if (entity is null) return NotFound();

        if (await db.VcsConnections.AnyAsync(c => c.Name == req.Name && c.Id != id, ct))
            return Conflict(new { error = localizer["Vcs_NameTaken", req.Name].Value });

        // The provider type is the stored one — this endpoint cannot change it. A connection whose
        // provider is no longer registered is refused rather than saved against an empty schema,
        // which would silently discard settings the absent adapter still needs.
        if (!providerFactory.AvailableProviders.Contains(entity.ProviderType))
            return BadRequest(new
            {
                error = localizer[
                    "Vcs_UnknownType", entity.ProviderType,
                    string.Join(", ", providerFactory.AvailableProviders)].Value
            });

        if (!ProviderSettingsBinder.TryBind(
                req.Settings, providerFactory.GetSettingsSchema(entity.ProviderType),
                entity.ProviderSettingsJson, protector, localizer,
                out var settingsJson, out var settingsError))
            return BadRequest(new { error = settingsError });

        entity.Name = req.Name;
        entity.ApiUrl = req.ApiUrl;
        entity.ProviderSettingsJson = settingsJson;

        // Only replace the token when one is supplied. The UI says "leave blank to keep
        // current", but this used to overwrite unconditionally — so renaming a connection
        // silently encrypted an empty string over a working token and broke every API call.
        if (!string.IsNullOrWhiteSpace(req.AccessToken))
            entity.EncryptedAccessToken = protector.Protect(req.AccessToken);

        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    /// <summary>
    /// Polls this connection's open merge requests now, emitting the same events the scheduled poller
    /// does — a manual refresh for a poll-mode connection between its timer ticks.
    /// </summary>
    /// <remarks>
    /// Works for any registered connection (a webhook-mode connection can be refreshed this way too),
    /// but the UI offers the button only for poll-mode types, whose events would otherwise wait for the
    /// next tick. The deterministic event id means re-polling an unchanged merge request is a no-op.
    /// </remarks>
    [HttpPost("{id:guid}/poll")]
    [Authorize(Policy = Permissions.ConfigEdit)]
    public async Task<IActionResult> Poll(
        Guid id, [FromServices] VcsConnectionPoller poller, CancellationToken ct)
    {
        var entity = await db.VcsConnections
            .Include(c => c.Repositories)
            .FirstOrDefaultAsync(c => c.Id == id, ct);
        if (entity is null) return NotFound();

        if (!providerFactory.AvailableProviders.Contains(entity.ProviderType))
            return BadRequest(new
            {
                error = localizer[
                    "Vcs_UnknownType", entity.ProviderType,
                    string.Join(", ", providerFactory.AvailableProviders)].Value
            });

        // A repository that cannot be read is reported, not thrown: the operator needs to see which one
        // is misconfigured (and what the others found), which a 500 hid.
        var result = await poller.PollAsync(entity, ct);
        return Ok(new
        {
            emitted = result.Emitted,
            failures = result.Failures.Select(f => new { repository = f.RepositoryExternalId, reason = f.Reason })
        });
    }

    [HttpDelete("{id:guid}")]
    [Authorize(Policy = Permissions.ConfigEdit)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var entity = await db.VcsConnections.FindAsync([id], ct);
        if (entity is null) return NotFound();

        if (await db.Repositories.AnyAsync(r => r.ConnectionId == id, ct))
            return Conflict(new { error = localizer["Vcs_HasRepositories"].Value });

        db.VcsConnections.Remove(entity);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }
}

/// <param name="VcsType">
/// The provider type, e.g. <c>gitlab-webhook</c> or <c>gitlab-poll</c>. Push versus poll is the type,
/// not a separate field: a polled connection is a poll type and carries its interval in its settings.
/// </param>
/// <param name="Settings">
/// Provider-specific settings, keyed as the chosen provider's schema declares them. Validated
/// against that schema — an undeclared key is refused rather than stored and ignored.
/// </param>
public record CreateVcsConnectionRequest(
    [Required, MaxLength(200)] string Name,
    [Required] string VcsType,
    [Required, MaxLength(500)] string ApiUrl,
    [Required, MaxLength(500)] string AccessToken,
    Dictionary<string, string?>? Settings = null);

/// <param name="AccessToken">Blank keeps the stored token.</param>
/// <param name="Settings">
/// Provider-specific settings. A blank value clears the setting, except for one the schema marks
/// secret, where blank keeps what is stored — the form cannot show a secret back, so an empty box
/// there means "untouched", the same convention <paramref name="AccessToken"/> uses.
/// </param>
public record UpdateVcsConnectionRequest(
    [Required, MaxLength(200)] string Name,
    [Required, MaxLength(500)] string ApiUrl,
    [MaxLength(500)] string? AccessToken = null,
    Dictionary<string, string?>? Settings = null);
