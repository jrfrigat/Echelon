using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ReleaseOrchestrator.Core.Entities;
using ReleaseOrchestrator.Infrastructure.Auth;
using ReleaseOrchestrator.Infrastructure.Persistence;

namespace ReleaseOrchestrator.Web.Controllers;

[ApiController]
[Route("api/repositories")]
[Authorize(Policy = Permissions.ReleasePlanView)]
public class RepositoriesController(AppDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] int page = 1, [FromQuery] int pageSize = Paging.DefaultPageSize, CancellationToken ct = default)
    {
        var paging = Paging.From(page, pageSize);
        var total = await db.Repositories.CountAsync(ct);

        // OrderBy is required for paging to be stable: without it SQL Server may return rows
        // in any order, so entries can repeat or vanish between pages.
        var items = await db.Repositories
            .OrderBy(r => r.Name).ThenBy(r => r.Id)
            .Select(r => new { r.Id, r.Name, r.ExternalId, r.ConnectionId, ConnectionName = r.Connection.Name, r.TrackerConnectionId, TrackerConnectionName = r.TrackerConnection != null ? r.TrackerConnection.Name : null })
            .Skip(paging.Skip).Take(paging.PageSize)
            .ToListAsync(ct);

        return Ok(new { Total = total, Page = paging.Page, PageSize = paging.PageSize, Items = items });
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var repository = await db.Repositories
            .Where(r => r.Id == id)
            .Select(r => new { r.Id, r.Name, r.ExternalId, r.ConnectionId, ConnectionName = r.Connection.Name, r.TrackerConnectionId, TrackerConnectionName = r.TrackerConnection != null ? r.TrackerConnection.Name : null })
            .FirstOrDefaultAsync(ct);

        return repository is null ? NotFound() : Ok(repository);
    }

    [HttpPost]
    [Authorize(Policy = Permissions.ConfigEdit)]
    public async Task<IActionResult> Create([FromBody] CreateRepositoryRequest req, CancellationToken ct)
    {
        if (!await db.VcsConnections.AnyAsync(c => c.Id == req.ConnectionId, ct))
            return BadRequest(new { error = $"VCS connection '{req.ConnectionId}' does not exist." });

        // Webhook routing resolves a repository by connection and external id and takes the first
        // match, so a second row with the same pair would silently divert merge requests.
        if (await db.Repositories.AnyAsync(r => r.ConnectionId == req.ConnectionId && r.ExternalId == req.ExternalId, ct))
            return Conflict(new { error = $"Repository '{req.ExternalId}' is already registered on this connection." });

        if (await ValidateTrackerAsync(req.TrackerConnectionId, ct) is { } trackerError)
            return BadRequest(new { error = trackerError });

        var entity = new Repository
        {
            Id = Guid.NewGuid(),
            Name = req.Name,
            ExternalId = req.ExternalId,
            ConnectionId = req.ConnectionId,
            TrackerConnectionId = req.TrackerConnectionId
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

        if (!await db.VcsConnections.AnyAsync(c => c.Id == req.ConnectionId, ct))
            return BadRequest(new { error = $"VCS connection '{req.ConnectionId}' does not exist." });

        if (await db.Repositories.AnyAsync(
                r => r.ConnectionId == req.ConnectionId && r.ExternalId == req.ExternalId && r.Id != id, ct))
            return Conflict(new { error = $"Repository '{req.ExternalId}' is already registered on this connection." });

        if (await ValidateTrackerAsync(req.TrackerConnectionId, ct) is { } trackerError)
            return BadRequest(new { error = trackerError });

        entity.Name = req.Name;
        entity.ExternalId = req.ExternalId;
        entity.ConnectionId = req.ConnectionId;
        entity.TrackerConnectionId = req.TrackerConnectionId;
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    /// <returns>An error message, or null when the value is acceptable.</returns>
    private async Task<string?> ValidateTrackerAsync(Guid? trackerConnectionId, CancellationToken ct)
    {
        if (trackerConnectionId is null) return null;

        return await db.TrackerConnections.AnyAsync(c => c.Id == trackerConnectionId, ct)
            ? null
            : $"Tracker connection '{trackerConnectionId}' does not exist.";
    }

    [HttpDelete("{id:guid}")]
    [Authorize(Policy = Permissions.ConfigEdit)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var entity = await db.Repositories.FindAsync([id], ct);
        if (entity is null) return NotFound();

        if (await db.MergeRequests.AnyAsync(mr => mr.RepositoryId == id, ct))
            return Conflict(new { error = "Repository still has merge requests; remove them first." });

        db.Repositories.Remove(entity);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }
}

public record CreateRepositoryRequest(
    [property: Required, MaxLength(300)] string Name,
    [property: Required, MaxLength(500)] string ExternalId,
    [property: Required] Guid ConnectionId,
    /// <summary>Which tracker this repository's branch issue keys belong to. Null falls back to a global match.</summary>
    Guid? TrackerConnectionId = null);
