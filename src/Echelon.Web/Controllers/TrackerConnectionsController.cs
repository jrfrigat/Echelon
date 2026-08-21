using System.ComponentModel.DataAnnotations;
using Echelon.Application.DTOs;
using Echelon.Infrastructure.Auth;
using Echelon.Infrastructure.Ingestion;
using Echelon.Infrastructure.Persistence;
using Echelon.Infrastructure.Persistence.Models;
using Echelon.Providers.Abstractions;
using Echelon.Providers.Abstractions.Tracker;
using Echelon.Web.Resources;
using Echelon.Web.Validation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;

namespace Echelon.Web.Controllers;

/// <summary>
/// The issue trackers this service reads tasks and their dependencies from. The tracker counterpart
/// of <see cref="VcsConnectionsController"/>, with the same rules: tokens are write-only, and the
/// provider type is fixed at creation.
/// </summary>
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
    private string[] AllowedApiHosts => config.GetSection("Security:AllowedApiHosts").Get<string[]>() ?? [];

    /// <summary>Configured tracker connections, by name. Never includes the access token.</summary>
    /// <param name="page">1-based page.</param>
    /// <param name="pageSize">Page size, clamped by <see cref="Paging"/>.</param>
    /// <param name="name">Substring of the connection name; the grid's "name" column filter.</param>
    /// <param name="type">Substring of the provider type; the grid's "type" column filter.</param>
    /// <param name="apiUrl">Substring of the API URL; the grid's "url" column filter.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <remarks>
    /// The filters run here, not in the browser, because the browser holds one page: filtering there
    /// searches the slice and presents the answer as though it had searched the list.
    /// </remarks>
    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = Paging.DefaultPageSize,
        [FromQuery] string? name = null,
        [FromQuery] string? type = null,
        [FromQuery] string? apiUrl = null,
        CancellationToken ct = default)
    {
        var paging = Paging.From(page, pageSize);
        var connections = ListFilter.Apply(db.TrackerConnections, name, type, apiUrl);
        var total = await connections.CountAsync(ct);

        // Projected to the database, then reshaped in memory: the settings bag is JSON in a
        // column, so it cannot be unpacked in SQL.
        var rows = await connections
            .OrderBy(c => c.Name).ThenBy(c => c.Id)
            .Select(c => new { c.Id, c.Name, c.ProviderType, c.ApiUrl, c.ProviderSettingsJson })
            .Skip(paging.Skip).Take(paging.PageSize)
            .ToListAsync(ct);

        var items = rows
            .Select(c => new TrackerConnectionDto(c.Id, c.Name, c.ProviderType, c.ApiUrl, ReadSettings(c.ProviderType, c.ProviderSettingsJson)))
            .ToList();

        return Ok(new PagedResult<TrackerConnectionDto>(total, paging.Page, paging.PageSize, items));
    }

    /// <summary>One tracker connection. Never includes the access token.</summary>
    /// <param name="id">The connection id.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var row = await db.TrackerConnections
            .Where(x => x.Id == id)
            .Select(x => new { x.Id, x.Name, x.ProviderType, x.ApiUrl, x.ProviderSettingsJson })
            .FirstOrDefaultAsync(ct);

        return row is null
            ? NotFound()
            : Ok(new TrackerConnectionDto(row.Id, row.Name, row.ProviderType, row.ApiUrl, ReadSettings(row.ProviderType, row.ProviderSettingsJson)));
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

    /// <summary>Creates a tracker connection.</summary>
    /// <param name="req">The connection to create, including its access token.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>201, 409 when the name is taken, or 400 for an unknown provider, a disallowed host, or a bad settings bag.</returns>
    [HttpPost]
    [Authorize(Policy = Permissions.ConfigEdit)]
    public async Task<IActionResult> Create([FromBody] CreateTrackerConnectionRequest req, CancellationToken ct)
    {
        // See VcsConnectionsController.Create: validated against the registered adapters.
        var providerType = ProviderKey.Normalize(req.TrackerType);
        if (!providerFactory.AvailableProviders.Contains(providerType))
        {
            return BadRequest(new
            {
                error = localizer["Tracker_UnknownType", req.TrackerType, string.Join(", ", providerFactory.AvailableProviders)].Value
            });
        }

        if (!ApiUrlValidator.TryValidate(req.ApiUrl, AllowedApiHosts, localizer, out var urlError))
        {
            return BadRequest(new { error = urlError });
        }

        if (await db.TrackerConnections.AnyAsync(c => c.Name == req.Name, ct))
        {
            return Conflict(new { error = localizer["Tracker_NameTaken", req.Name].Value });
        }

        if (!ProviderSettingsBinder.TryBind(
                req.Settings, providerFactory.GetSettingsSchema(providerType),
                existingJson: null, protector, localizer, out var settingsJson, out var settingsError))
        {
            return BadRequest(new { error = settingsError });
        }

        var entity = new TrackerConnection
        {
            Id = Guid.NewGuid(),
            Name = req.Name,
            ProviderType = providerType,
            ApiUrl = req.ApiUrl,
            ProviderSettingsJson = settingsJson,
            EncryptedAccessToken = protector.Protect(req.AccessToken)
        };

        db.TrackerConnections.Add(entity);
        await db.SaveChangesAsync(ct);
        return CreatedAtAction(nameof(Get), new { id = entity.Id }, new { entity.Id, entity.Name });
    }

    /// <summary>Updates a tracker connection. The provider type is fixed at creation.</summary>
    /// <param name="id">The connection to update.</param>
    /// <param name="req">The new values; a blank access token keeps the stored one.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpPut("{id:guid}")]
    [Authorize(Policy = Permissions.ConfigEdit)]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateTrackerConnectionRequest req, CancellationToken ct)
    {
        if (!ApiUrlValidator.TryValidate(req.ApiUrl, AllowedApiHosts, localizer, out var urlError))
        {
            return BadRequest(new { error = urlError });
        }

        var entity = await db.TrackerConnections.FindAsync([id], ct);
        if (entity is null)
        {
            return NotFound();
        }

        if (await db.TrackerConnections.AnyAsync(c => c.Name == req.Name && c.Id != id, ct))
        {
            return Conflict(new { error = localizer["Tracker_NameTaken", req.Name].Value });
        }

        // The provider type is the stored one - this endpoint cannot change it. A connection whose
        // provider is no longer registered is refused rather than saved against an empty schema,
        // which would silently discard settings the absent adapter still needs.
        if (!providerFactory.AvailableProviders.Contains(entity.ProviderType))
        {
            return BadRequest(new
            {
                error = localizer[
                    "Tracker_UnknownType", entity.ProviderType,
                    string.Join(", ", providerFactory.AvailableProviders)].Value
            });
        }

        if (!ProviderSettingsBinder.TryBind(
                req.Settings, providerFactory.GetSettingsSchema(entity.ProviderType),
                entity.ProviderSettingsJson, protector, localizer,
                out var settingsJson, out var settingsError))
        {
            return BadRequest(new { error = settingsError });
        }

        entity.Name = req.Name;
        entity.ApiUrl = req.ApiUrl;
        entity.ProviderSettingsJson = settingsJson;

        // Blank keeps the stored token - see the UI's "leave blank to keep current".
        if (!string.IsNullOrWhiteSpace(req.AccessToken))
        {
            entity.EncryptedAccessToken = protector.Protect(req.AccessToken);
        }

        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    /// <summary>
    /// Polls this connection now: asks the tracker which issues are open and re-reads the tasks already
    /// known to be open, emitting the same events the scheduled sweep does.
    /// </summary>
    /// <remarks>
    /// Works for any registered connection - a webhook-mode one can be refreshed this way too - but the
    /// UI offers the button only for poll-mode types, whose tasks would otherwise wait for the next
    /// tick. Re-requesting a sync for an unchanged task is a no-op, so polling twice costs a tracker
    /// read and changes nothing.
    /// </remarks>
    /// <param name="id">The connection to poll.</param>
    /// <param name="poller">The shared per-connection poller, so manual and scheduled sweeps cannot diverge.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>How many syncs were requested, how many tasks were new, and why the tracker could not be searched when it could not.</returns>
    [HttpPost("{id:guid}/poll")]
    [Authorize(Policy = Permissions.ConfigEdit)]
    public async Task<IActionResult> Poll(
        Guid id, [FromServices] TrackerConnectionPoller poller, CancellationToken ct)
    {
        var entity = await db.TrackerConnections
            .FirstOrDefaultAsync(c => c.Id == id, ct);
        if (entity is null)
        {
            return NotFound();
        }

        if (!providerFactory.AvailableProviders.Contains(entity.ProviderType))
        {
            return BadRequest(new
            {
                error = localizer[
                    "Tracker_UnknownType", entity.ProviderType,
                    string.Join(", ", providerFactory.AvailableProviders)].Value
            });
        }

        // A tracker that cannot be searched is reported in the body, not thrown: the tasks already known
        // were still re-read, and a 500 would hide both that and the tracker's own explanation - which
        // is usually the missing setting that says which queues to sweep.
        return Ok(await poller.PollAsync(entity, ct));
    }

    /// <summary>Removes a tracker connection. Refused while tasks still point at it.</summary>
    /// <param name="id">The connection to remove.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpDelete("{id:guid}")]
    [Authorize(Policy = Permissions.ConfigEdit)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var entity = await db.TrackerConnections.FindAsync([id], ct);
        if (entity is null)
        {
            return NotFound();
        }

        if (await db.Tasks.AnyAsync(t => t.TrackerConnectionId == id, ct))
        {
            return Conflict(new { error = localizer["Tracker_HasTasks"].Value });
        }

        db.TrackerConnections.Remove(entity);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }
}

/// <summary>A new tracker connection.</summary>
/// <param name="Name">Unique display name; repositories refer to it when scoping task keys.</param>
/// <param name="TrackerType">The provider type, validated against the registered adapters.</param>
/// <param name="ApiUrl">The tracker's API root. Must resolve to an allowed host.</param>
/// <param name="AccessToken">The credential, stored encrypted and never returned.</param>
/// <param name="Settings">
/// Provider-specific settings, keyed as the chosen provider's schema declares them. Validated
/// against that schema - an undeclared key is refused rather than stored and ignored.
/// </param>
public record CreateTrackerConnectionRequest(
    [Required, MaxLength(200)] string Name,
    [Required] string TrackerType,
    [Required, MaxLength(500)] string ApiUrl,
    Dictionary<string, string?>? Settings,
    [Required, MaxLength(500)] string AccessToken);

/// <summary>Changes to an existing tracker connection. The provider type is not changeable.</summary>
/// <param name="Name">Unique display name.</param>
/// <param name="ApiUrl">The tracker's API root. Must resolve to an allowed host.</param>
/// <param name="Settings">
/// Provider-specific settings. A blank value clears the setting, except for one the schema marks
/// secret, where blank keeps what is stored - the form cannot show a secret back, so an empty box
/// there means "untouched", the same convention <paramref name="AccessToken"/> uses.
/// </param>
/// <param name="AccessToken">Blank keeps the stored token.</param>
public record UpdateTrackerConnectionRequest(
    [Required, MaxLength(200)] string Name,
    [Required, MaxLength(500)] string ApiUrl,
    Dictionary<string, string?>? Settings,
    [MaxLength(500)] string? AccessToken = null);
