using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ReleaseOrchestrator.Core.Entities;
using ReleaseOrchestrator.Infrastructure.Auth;
using ReleaseOrchestrator.Infrastructure.Persistence;

namespace ReleaseOrchestrator.Web.Controllers;

[ApiController]
[Route("api/repositories")]
[Authorize]
public class RepositoriesController(AppDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] int page = 1, [FromQuery] int pageSize = 50, CancellationToken ct = default)
    {
        var total = await db.Repositories.CountAsync(ct);
        var items = await db.Repositories
            .Include(r => r.Connection)
            .Select(r => new { r.Id, r.Name, r.ExternalId, r.ConnectionId, ConnectionName = r.Connection.Name })
            .Skip((page - 1) * pageSize).Take(pageSize)
            .ToListAsync(ct);
        return Ok(new { Total = total, Page = page, PageSize = pageSize, Items = items });
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var r = await db.Repositories.Include(x => x.Connection).FirstOrDefaultAsync(x => x.Id == id, ct);
        return r is null ? NotFound() : Ok(new { r.Id, r.Name, r.ExternalId, r.ConnectionId, ConnectionName = r.Connection.Name });
    }

    [HttpPost]
    [Authorize(Policy = Permissions.ConfigEdit)]
    public async Task<IActionResult> Create([FromBody] CreateRepositoryRequest req, CancellationToken ct)
    {
        var connectionExists = await db.VcsConnections.AnyAsync(c => c.Id == req.ConnectionId, ct);
        if (!connectionExists) return BadRequest("VCS connection not found.");

        var entity = new Repository
        {
            Id = Guid.NewGuid(),
            Name = req.Name,
            ExternalId = req.ExternalId,
            ConnectionId = req.ConnectionId
        };
        db.Repositories.Add(entity);
        await db.SaveChangesAsync(ct);
        return CreatedAtAction(nameof(Get), new { id = entity.Id }, new { entity.Id, entity.Name });
    }

    [HttpPut("{id:guid}")]
    [Authorize(Policy = Permissions.ConfigEdit)]
    public async Task<IActionResult> Update(Guid id, [FromBody] CreateRepositoryRequest req, CancellationToken ct)
    {
        var entity = await db.Repositories.FindAsync([id], ct);
        if (entity is null) return NotFound();

        var connectionExists = await db.VcsConnections.AnyAsync(c => c.Id == req.ConnectionId, ct);
        if (!connectionExists) return BadRequest("VCS connection not found.");

        entity.Name = req.Name;
        entity.ExternalId = req.ExternalId;
        entity.ConnectionId = req.ConnectionId;
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpDelete("{id:guid}")]
    [Authorize(Policy = Permissions.ConfigEdit)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var entity = await db.Repositories.FindAsync([id], ct);
        if (entity is null) return NotFound();
        db.Repositories.Remove(entity);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }
}

public record CreateRepositoryRequest(string Name, string ExternalId, Guid ConnectionId);
