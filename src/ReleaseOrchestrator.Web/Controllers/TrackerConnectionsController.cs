using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Localization;
using ReleaseOrchestrator.Core.Entities;
using ReleaseOrchestrator.Infrastructure.Auth;
using ReleaseOrchestrator.Infrastructure.Persistence;
using ReleaseOrchestrator.Providers.Abstractions;
using ReleaseOrchestrator.Providers.Abstractions.Tracker;
using ReleaseOrchestrator.Web.Resources;
using ReleaseOrchestrator.Web.Validation;

namespace ReleaseOrchestrator.Web.Controllers;

[ApiController]
[Route("api/tracker-connections")]
[Authorize(Policy = Permissions.ReleasePlanView)]
public class TrackerConnectionsController(
    AppDbContext db,
    TokenProtector protector,
    ITrackerProviderFactory providerFactory,
    IConfiguration config,
    IStringLocalizer<ApiStrings> localizer) : ControllerBase
{
    /// <summary>
    /// The settings key the wire's <c>orgId</c> field maps to.
    /// </summary>
    /// <remarks>
    /// A compatibility shim, and the last place a provider's vocabulary appears outside its own
    /// adapter. The entity now stores an opaque settings bag, but the HTTP contract still has an
    /// <c>orgId</c> field and the PWA still sends it. Replacing that field with a generic
    /// <c>providerSettings</c> object is a UI change, so it is deliberately not bundled here.
    /// Nothing reads this key's meaning — only the Yandex.Tracker adapter does.
    /// </remarks>
    private const string OrgIdSettingKey = "orgId";

    private string[] AllowedApiHosts => config.GetSection("Security:AllowedApiHosts").Get<string[]>() ?? [];

    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] int page = 1, [FromQuery] int pageSize = Paging.DefaultPageSize, CancellationToken ct = default)
    {
        var paging = Paging.From(page, pageSize);
        var total = await db.TrackerConnections.CountAsync(ct);

        // Projected to the database, then reshaped in memory: the settings bag is JSON in a
        // column, so orgId cannot be read out of it in SQL.
        var rows = await db.TrackerConnections
            .OrderBy(c => c.Name).ThenBy(c => c.Id)
            .Select(c => new { c.Id, c.Name, c.ProviderType, c.ApiUrl, c.ProviderSettingsJson })
            .Skip(paging.Skip).Take(paging.PageSize)
            .ToListAsync(ct);

        var items = rows
            .Select(c => new { c.Id, c.Name, TrackerType = c.ProviderType, c.ApiUrl, OrgId = ReadOrgId(c.ProviderSettingsJson) })
            .ToList();

        return Ok(new { Total = total, Page = paging.Page, PageSize = paging.PageSize, Items = items });
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var row = await db.TrackerConnections
            .Where(x => x.Id == id)
            .Select(x => new { x.Id, x.Name, x.ProviderType, x.ApiUrl, x.ProviderSettingsJson })
            .FirstOrDefaultAsync(ct);

        return row is null
            ? NotFound()
            : Ok(new { row.Id, row.Name, TrackerType = row.ProviderType, row.ApiUrl, OrgId = ReadOrgId(row.ProviderSettingsJson) });
    }

    /// <summary>Reads the wire's orgId back out of the stored settings bag.</summary>
    private static string? ReadOrgId(string? providerSettingsJson)
    {
        if (string.IsNullOrWhiteSpace(providerSettingsJson)) return null;

        try
        {
            var settings = JsonSerializer.Deserialize<Dictionary<string, string?>>(providerSettingsJson);
            return settings is not null && settings.TryGetValue(OrgIdSettingKey, out var orgId) ? orgId : null;
        }
        catch (JsonException)
        {
            // A row this endpoint cannot parse is still worth listing: swallowing the whole
            // connection because one setting is malformed would hide the row an operator needs to
            // fix. The provider factory raises the real error when the connection is used.
            return null;
        }
    }

    /// <summary>Writes the wire's orgId into the settings bag, dropping it when blank.</summary>
    private static string? WriteOrgId(string? orgId) =>
        string.IsNullOrWhiteSpace(orgId)
            ? null
            : JsonSerializer.Serialize(new Dictionary<string, string?> { [OrgIdSettingKey] = orgId.Trim() });

    [HttpPost]
    [Authorize(Policy = Permissions.ConfigEdit)]
    public async Task<IActionResult> Create([FromBody] CreateTrackerConnectionRequest req, CancellationToken ct)
    {
        // See VcsConnectionsController.Create: validated against the registered adapters.
        var providerType = ProviderKey.Normalize(req.TrackerType);
        if (!providerFactory.AvailableProviders.Contains(providerType))
            return BadRequest(new
            {
                error = localizer["Tracker_UnknownType", req.TrackerType, string.Join(", ", providerFactory.AvailableProviders)].Value
            });

        if (!ApiUrlValidator.TryValidate(req.ApiUrl, AllowedApiHosts, localizer, out var urlError))
            return BadRequest(new { error = urlError });

        if (await db.TrackerConnections.AnyAsync(c => c.Name == req.Name, ct))
            return Conflict(new { error = localizer["Tracker_NameTaken", req.Name].Value });

        var entity = new TrackerConnection
        {
            Id = Guid.NewGuid(),
            Name = req.Name,
            ProviderType = providerType,
            ApiUrl = req.ApiUrl,
            ProviderSettingsJson = WriteOrgId(req.OrgId),
            EncryptedAccessToken = protector.Protect(req.AccessToken)
        };

        db.TrackerConnections.Add(entity);
        await db.SaveChangesAsync(ct);
        return CreatedAtAction(nameof(Get), new { id = entity.Id }, new { entity.Id, entity.Name });
    }

    [HttpPut("{id:guid}")]
    [Authorize(Policy = Permissions.ConfigEdit)]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateTrackerConnectionRequest req, CancellationToken ct)
    {
        if (!ApiUrlValidator.TryValidate(req.ApiUrl, AllowedApiHosts, localizer, out var urlError))
            return BadRequest(new { error = urlError });

        var entity = await db.TrackerConnections.FindAsync([id], ct);
        if (entity is null) return NotFound();

        if (await db.TrackerConnections.AnyAsync(c => c.Name == req.Name && c.Id != id, ct))
            return Conflict(new { error = localizer["Tracker_NameTaken", req.Name].Value });

        entity.Name = req.Name;
        entity.ApiUrl = req.ApiUrl;
        entity.ProviderSettingsJson = WriteOrgId(req.OrgId);

        // Blank keeps the stored token — see the UI's "leave blank to keep current".
        if (!string.IsNullOrWhiteSpace(req.AccessToken))
            entity.EncryptedAccessToken = protector.Protect(req.AccessToken);

        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpDelete("{id:guid}")]
    [Authorize(Policy = Permissions.ConfigEdit)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var entity = await db.TrackerConnections.FindAsync([id], ct);
        if (entity is null) return NotFound();

        if (await db.Tasks.AnyAsync(t => t.TrackerConnectionId == id, ct))
            return Conflict(new { error = localizer["Tracker_HasTasks"].Value });

        db.TrackerConnections.Remove(entity);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }
}

public record CreateTrackerConnectionRequest(
    [property: Required, MaxLength(200)] string Name,
    [property: Required] string TrackerType,
    [property: Required, MaxLength(500)] string ApiUrl,
    [property: MaxLength(200)] string? OrgId,
    [property: Required, MaxLength(500)] string AccessToken);

/// <param name="AccessToken">Blank keeps the stored token.</param>
public record UpdateTrackerConnectionRequest(
    [property: Required, MaxLength(200)] string Name,
    [property: Required, MaxLength(500)] string ApiUrl,
    [property: MaxLength(200)] string? OrgId,
    [property: MaxLength(500)] string? AccessToken = null);
