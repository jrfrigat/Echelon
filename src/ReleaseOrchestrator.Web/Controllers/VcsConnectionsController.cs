using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ReleaseOrchestrator.Core.Entities;
using ReleaseOrchestrator.Core.Enums;
using ReleaseOrchestrator.Infrastructure.Auth;
using ReleaseOrchestrator.Infrastructure.Persistence;

namespace ReleaseOrchestrator.Web.Controllers;

[ApiController]
[Route("api/vcs-connections")]
[Authorize]
public class VcsConnectionsController(AppDbContext db, TokenProtector protector) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] int page = 1, [FromQuery] int pageSize = 50, CancellationToken ct = default)
    {
        var total = await db.VcsConnections.CountAsync(ct);
        var items = await db.VcsConnections
            .Select(c => new { c.Id, c.Name, c.VcsType, c.ApiUrl })
            .Skip((page - 1) * pageSize).Take(pageSize)
            .ToListAsync(ct);
        return Ok(new { Total = total, Page = page, PageSize = pageSize, Items = items });
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var c = await db.VcsConnections.FindAsync([id], ct);
        return c is null ? NotFound() : Ok(new { c.Id, c.Name, c.VcsType, c.ApiUrl });
    }

    [HttpPost]
    [Authorize(Policy = Permissions.ConfigEdit)]
    public async Task<IActionResult> Create([FromBody] UpsertVcsConnectionRequest req, CancellationToken ct)
    {
        var entity = new VcsConnection
        {
            Id = Guid.NewGuid(),
            Name = req.Name,
            VcsType = Enum.Parse<VcsType>(req.VcsType),
            ApiUrl = req.ApiUrl,
            EncryptedAccessToken = protector.Protect(req.AccessToken)
        };
        db.VcsConnections.Add(entity);
        await db.SaveChangesAsync(ct);
        return CreatedAtAction(nameof(Get), new { id = entity.Id }, new { entity.Id, entity.Name });
    }

    [HttpPut("{id:guid}")]
    [Authorize(Policy = Permissions.ConfigEdit)]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpsertVcsConnectionRequest req, CancellationToken ct)
    {
        var entity = await db.VcsConnections.FindAsync([id], ct);
        if (entity is null) return NotFound();
        entity.Name = req.Name;
        entity.ApiUrl = req.ApiUrl;
        entity.EncryptedAccessToken = protector.Protect(req.AccessToken);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpDelete("{id:guid}")]
    [Authorize(Policy = Permissions.ConfigEdit)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var entity = await db.VcsConnections.FindAsync([id], ct);
        if (entity is null) return NotFound();
        db.VcsConnections.Remove(entity);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }
}

public record UpsertVcsConnectionRequest(string Name, string VcsType, string ApiUrl, string AccessToken);
