using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ReleaseOrchestrator.Core.Entities;
using ReleaseOrchestrator.Core.Enums;
using ReleaseOrchestrator.Infrastructure.Auth;
using ReleaseOrchestrator.Infrastructure.Persistence;

namespace ReleaseOrchestrator.Web.Controllers;

[ApiController]
[Route("api/tracker-connections")]
[Authorize]
public class TrackerConnectionsController(AppDbContext db, TokenProtector protector) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] int page = 1, [FromQuery] int pageSize = 50, CancellationToken ct = default)
    {
        var total = await db.TrackerConnections.CountAsync(ct);
        var items = await db.TrackerConnections
            .Select(c => new { c.Id, c.Name, c.TrackerType, c.ApiUrl, c.OrgId })
            .Skip((page - 1) * pageSize).Take(pageSize)
            .ToListAsync(ct);
        return Ok(new { Total = total, Page = page, PageSize = pageSize, Items = items });
    }

    [HttpPost]
    [Authorize(Policy = Permissions.ConfigEdit)]
    public async Task<IActionResult> Create([FromBody] UpsertTrackerConnectionRequest req, CancellationToken ct)
    {
        var entity = new TrackerConnection
        {
            Id = Guid.NewGuid(),
            Name = req.Name,
            TrackerType = Enum.Parse<TrackerType>(req.TrackerType),
            ApiUrl = req.ApiUrl,
            OrgId = req.OrgId,
            EncryptedAccessToken = protector.Protect(req.AccessToken)
        };
        db.TrackerConnections.Add(entity);
        await db.SaveChangesAsync(ct);
        return CreatedAtAction(nameof(List), new { entity.Id, entity.Name });
    }

    [HttpPut("{id:guid}")]
    [Authorize(Policy = Permissions.ConfigEdit)]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpsertTrackerConnectionRequest req, CancellationToken ct)
    {
        var entity = await db.TrackerConnections.FindAsync([id], ct);
        if (entity is null) return NotFound();
        entity.Name = req.Name;
        entity.ApiUrl = req.ApiUrl;
        entity.OrgId = req.OrgId;
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
        db.TrackerConnections.Remove(entity);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }
}

public record UpsertTrackerConnectionRequest(string Name, string TrackerType, string ApiUrl, string? OrgId, string AccessToken);
