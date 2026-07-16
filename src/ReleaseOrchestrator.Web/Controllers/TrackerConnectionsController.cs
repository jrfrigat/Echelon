using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using ReleaseOrchestrator.Core.Entities;
using ReleaseOrchestrator.Core.Enums;
using ReleaseOrchestrator.Infrastructure.Auth;
using ReleaseOrchestrator.Infrastructure.Persistence;
using ReleaseOrchestrator.Web.Validation;

namespace ReleaseOrchestrator.Web.Controllers;

[ApiController]
[Route("api/tracker-connections")]
[Authorize(Policy = Permissions.ReleasePlanView)]
public class TrackerConnectionsController(
    AppDbContext db,
    TokenProtector protector,
    IConfiguration config) : ControllerBase
{
    private string[] AllowedApiHosts => config.GetSection("Security:AllowedApiHosts").Get<string[]>() ?? [];

    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] int page = 1, [FromQuery] int pageSize = Paging.DefaultPageSize, CancellationToken ct = default)
    {
        var paging = Paging.From(page, pageSize);
        var total = await db.TrackerConnections.CountAsync(ct);

        var items = await db.TrackerConnections
            .OrderBy(c => c.Name).ThenBy(c => c.Id)
            .Select(c => new { c.Id, c.Name, c.TrackerType, c.ApiUrl, c.OrgId })
            .Skip(paging.Skip).Take(paging.PageSize)
            .ToListAsync(ct);

        return Ok(new { Total = total, Page = paging.Page, PageSize = paging.PageSize, Items = items });
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var c = await db.TrackerConnections
            .Where(x => x.Id == id)
            .Select(x => new { x.Id, x.Name, x.TrackerType, x.ApiUrl, x.OrgId })
            .FirstOrDefaultAsync(ct);

        return c is null ? NotFound() : Ok(c);
    }

    [HttpPost]
    [Authorize(Policy = Permissions.ConfigEdit)]
    public async Task<IActionResult> Create([FromBody] CreateTrackerConnectionRequest req, CancellationToken ct)
    {
        if (!Enum.TryParse<TrackerType>(req.TrackerType, true, out var trackerType))
            return BadRequest(new { error = $"Unknown trackerType '{req.TrackerType}'. Valid: {string.Join(", ", Enum.GetNames<TrackerType>())}" });

        if (!ApiUrlValidator.TryValidate(req.ApiUrl, AllowedApiHosts, out var urlError))
            return BadRequest(new { error = urlError });

        if (await db.TrackerConnections.AnyAsync(c => c.Name == req.Name, ct))
            return Conflict(new { error = $"A tracker connection named '{req.Name}' already exists." });

        var entity = new TrackerConnection
        {
            Id = Guid.NewGuid(),
            Name = req.Name,
            TrackerType = trackerType,
            ApiUrl = req.ApiUrl,
            OrgId = req.OrgId,
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
        if (!ApiUrlValidator.TryValidate(req.ApiUrl, AllowedApiHosts, out var urlError))
            return BadRequest(new { error = urlError });

        var entity = await db.TrackerConnections.FindAsync([id], ct);
        if (entity is null) return NotFound();

        if (await db.TrackerConnections.AnyAsync(c => c.Name == req.Name && c.Id != id, ct))
            return Conflict(new { error = $"A tracker connection named '{req.Name}' already exists." });

        entity.Name = req.Name;
        entity.ApiUrl = req.ApiUrl;
        entity.OrgId = req.OrgId;

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
            return Conflict(new { error = "Connection still has tasks; archive them first." });

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

public record UpdateTrackerConnectionRequest(
    [property: Required, MaxLength(200)] string Name,
    [property: Required, MaxLength(500)] string ApiUrl,
    [property: MaxLength(200)] string? OrgId,
    /// <summary>Blank keeps the stored token.</summary>
    [property: MaxLength(500)] string? AccessToken = null);
