using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Localization;
using ReleaseOrchestrator.Core.Entities;
using ReleaseOrchestrator.Infrastructure.Auth;
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
        var items = await db.VcsConnections
            .OrderBy(c => c.Name).ThenBy(c => c.Id)
            .Select(c => new { c.Id, c.Name, VcsType = c.ProviderType, c.ApiUrl, c.ReadyForDeployLabel })
            .Skip(paging.Skip).Take(paging.PageSize)
            .ToListAsync(ct);

        return Ok(new { Total = total, Page = paging.Page, PageSize = paging.PageSize, Items = items });
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var c = await db.VcsConnections
            .Where(x => x.Id == id)
            .Select(x => new { x.Id, x.Name, VcsType = x.ProviderType, x.ApiUrl, x.ReadyForDeployLabel })
            .FirstOrDefaultAsync(ct);

        return c is null ? NotFound() : Ok(c);
    }

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

        var entity = new VcsConnection
        {
            Id = Guid.NewGuid(),
            Name = req.Name,
            ProviderType = providerType,
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
        if (!ApiUrlValidator.TryValidate(req.ApiUrl, AllowedApiHosts, localizer, out var urlError))
            return BadRequest(new { error = urlError });

        var entity = await db.VcsConnections.FindAsync([id], ct);
        if (entity is null) return NotFound();

        if (await db.VcsConnections.AnyAsync(c => c.Name == req.Name && c.Id != id, ct))
            return Conflict(new { error = localizer["Vcs_NameTaken", req.Name].Value });

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
            return Conflict(new { error = localizer["Vcs_HasRepositories"].Value });

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
