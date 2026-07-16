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
[Route("api/vcs-connections")]
[Authorize(Policy = Permissions.ReleasePlanView)]
public class VcsConnectionsController(
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
        var total = await db.VcsConnections.CountAsync(ct);

        // OrderBy is required for paging to be stable: without it SQL Server may return rows
        // in any order, so entries can repeat or vanish between pages.
        var items = await db.VcsConnections
            .OrderBy(c => c.Name).ThenBy(c => c.Id)
            .Select(c => new { c.Id, c.Name, c.VcsType, c.ApiUrl, c.ReadyForDeployLabel })
            .Skip(paging.Skip).Take(paging.PageSize)
            .ToListAsync(ct);

        return Ok(new { Total = total, Page = paging.Page, PageSize = paging.PageSize, Items = items });
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var c = await db.VcsConnections
            .Where(x => x.Id == id)
            .Select(x => new { x.Id, x.Name, x.VcsType, x.ApiUrl, x.ReadyForDeployLabel })
            .FirstOrDefaultAsync(ct);

        return c is null ? NotFound() : Ok(c);
    }

    [HttpPost]
    [Authorize(Policy = Permissions.ConfigEdit)]
    public async Task<IActionResult> Create([FromBody] CreateVcsConnectionRequest req, CancellationToken ct)
    {
        if (!Enum.TryParse<VcsType>(req.VcsType, true, out var vcsType))
            return BadRequest(new { error = $"Unknown vcsType '{req.VcsType}'. Valid: {string.Join(", ", Enum.GetNames<VcsType>())}" });

        if (!ApiUrlValidator.TryValidate(req.ApiUrl, AllowedApiHosts, out var urlError))
            return BadRequest(new { error = urlError });

        if (await db.VcsConnections.AnyAsync(c => c.Name == req.Name, ct))
            return Conflict(new { error = $"A VCS connection named '{req.Name}' already exists." });

        var entity = new VcsConnection
        {
            Id = Guid.NewGuid(),
            Name = req.Name,
            VcsType = vcsType,
            ApiUrl = req.ApiUrl,
            ReadyForDeployLabel = req.ReadyForDeployLabel,
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
        if (!ApiUrlValidator.TryValidate(req.ApiUrl, AllowedApiHosts, out var urlError))
            return BadRequest(new { error = urlError });

        var entity = await db.VcsConnections.FindAsync([id], ct);
        if (entity is null) return NotFound();

        if (await db.VcsConnections.AnyAsync(c => c.Name == req.Name && c.Id != id, ct))
            return Conflict(new { error = $"A VCS connection named '{req.Name}' already exists." });

        entity.Name = req.Name;
        entity.ApiUrl = req.ApiUrl;
        entity.ReadyForDeployLabel = req.ReadyForDeployLabel;

        // Only replace the token when one is supplied. The UI says "leave blank to keep
        // current", but this used to overwrite unconditionally — so renaming a connection
        // silently encrypted an empty string over a working token and broke every API call.
        if (!string.IsNullOrWhiteSpace(req.AccessToken))
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

        if (await db.Repositories.AnyAsync(r => r.ConnectionId == id, ct))
            return Conflict(new { error = "Connection still has repositories; remove them first." });

        db.VcsConnections.Remove(entity);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }
}

public record CreateVcsConnectionRequest(
    [property: Required, MaxLength(200)] string Name,
    [property: Required] string VcsType,
    [property: Required, MaxLength(500)] string ApiUrl,
    [property: Required, MaxLength(500)] string AccessToken,
    [property: MaxLength(200)] string? ReadyForDeployLabel = VcsConnection.DefaultReadyForDeployLabel);

/// <param name="AccessToken">Blank keeps the stored token.</param>
/// <param name="ReadyForDeployLabel">Blank disables label-driven promotion for this connection.</param>
public record UpdateVcsConnectionRequest(
    [property: Required, MaxLength(200)] string Name,
    [property: Required, MaxLength(500)] string ApiUrl,
    [property: MaxLength(500)] string? AccessToken = null,
    [property: MaxLength(200)] string? ReadyForDeployLabel = VcsConnection.DefaultReadyForDeployLabel);
