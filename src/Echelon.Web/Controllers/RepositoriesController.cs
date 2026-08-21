using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Localization;
using Echelon.Application.DTOs;
using Echelon.Infrastructure.Persistence.Models;
using Echelon.Infrastructure.Auth;
using Echelon.Infrastructure.Persistence;
using Echelon.Web.Resources;

namespace Echelon.Web.Controllers;

/// <summary>
/// The repositories this service watches: which VCS connection reaches them, and which tracker
/// their branch issue keys belong to.
/// </summary>
[ApiController]
[Route("api/repositories")]
[Authorize(Policy = Permissions.ReleasePlanView)]
public class RepositoriesController(AppDbContext db, IStringLocalizer<ApiStrings> localizer) : ControllerBase
{
    /// <summary>Registered repositories, by name.</summary>
    /// <param name="page">1-based page.</param>
    /// <param name="pageSize">Page size, clamped by <see cref="Paging"/>.</param>
    /// <param name="name">Substring of the repository name; the grid's "name" column filter.</param>
    /// <param name="externalId">Substring of the provider-side id; the grid's "external" column filter.</param>
    /// <param name="connection">Substring of the VCS connection name; the grid's "connection" column filter.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <remarks>See <see cref="ListFilter"/> for why the filtering is here and not in the grid.</remarks>
    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = Paging.DefaultPageSize,
        [FromQuery] string? name = null,
        [FromQuery] string? externalId = null,
        [FromQuery] string? connection = null,
        CancellationToken ct = default)
    {
        var paging = Paging.From(page, pageSize);
        var repositories = ListFilter.Apply(db.Repositories, name, externalId, connection);
        var total = await repositories.CountAsync(ct);

        // OrderBy is required for paging to be stable: without it SQL Server may return rows
        // in any order, so entries can repeat or vanish between pages.
        var items = await repositories
            .OrderBy(r => r.Name).ThenBy(r => r.Id)
            .Select(r => new RepositoryDto(
                r.Id,
                r.Name,
                r.ExternalId,
                r.ConnectionId,
                r.Connection.Name,
                r.TrackerConnectionId,
                r.TrackerConnection != null ? r.TrackerConnection.Name : null))
            .Skip(paging.Skip).Take(paging.PageSize)
            .ToListAsync(ct);

        return Ok(new PagedResult<RepositoryDto>(total, paging.Page, paging.PageSize, items));
    }

    /// <summary>One repository.</summary>
    /// <param name="id">The repository id.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var repository = await db.Repositories
            .Where(r => r.Id == id)
            .Select(r => new RepositoryDto(
                r.Id,
                r.Name,
                r.ExternalId,
                r.ConnectionId,
                r.Connection.Name,
                r.TrackerConnectionId,
                r.TrackerConnection != null ? r.TrackerConnection.Name : null))
            .FirstOrDefaultAsync(ct);

        return repository is null ? NotFound() : Ok(repository);
    }

    /// <summary>Registers a repository.</summary>
    /// <param name="req">The repository to register.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// 201, or 409 when the connection already has a repository with that external id - webhook
    /// routing takes the first match, so a duplicate pair silently diverts merge requests.
    /// </returns>
    [HttpPost]
    [Authorize(Policy = Permissions.ConfigEdit)]
    public async Task<IActionResult> Create([FromBody] CreateRepositoryRequest req, CancellationToken ct)
    {
        if (!await db.VcsConnections.AnyAsync(c => c.Id == req.ConnectionId, ct))
            return BadRequest(new { error = localizer["Repo_ConnectionNotFound", req.ConnectionId].Value });

        // Webhook routing resolves a repository by connection and external id and takes the first
        // match, so a second row with the same pair would silently divert merge requests.
        if (await db.Repositories.AnyAsync(r => r.ConnectionId == req.ConnectionId && r.ExternalId == req.ExternalId, ct))
            return Conflict(new { error = localizer["Repo_AlreadyRegistered", req.ExternalId].Value });

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

    /// <summary>Replaces a repository's registration.</summary>
    /// <param name="id">The repository to update.</param>
    /// <param name="req">The new values. Every field is replaced.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpPut("{id:guid}")]
    [Authorize(Policy = Permissions.ConfigEdit)]
    public async Task<IActionResult> Update(Guid id, [FromBody] CreateRepositoryRequest req, CancellationToken ct)
    {
        var entity = await db.Repositories.FindAsync([id], ct);
        if (entity is null) return NotFound();

        if (!await db.VcsConnections.AnyAsync(c => c.Id == req.ConnectionId, ct))
            return BadRequest(new { error = localizer["Repo_ConnectionNotFound", req.ConnectionId].Value });

        if (await db.Repositories.AnyAsync(
                r => r.ConnectionId == req.ConnectionId && r.ExternalId == req.ExternalId && r.Id != id, ct))
            return Conflict(new { error = localizer["Repo_AlreadyRegistered", req.ExternalId].Value });

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
            : localizer["Repo_TrackerConnectionNotFound", trackerConnectionId].Value;
    }

    /// <summary>Removes a repository. Refused while it still holds merge requests.</summary>
    /// <param name="id">The repository to remove.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpDelete("{id:guid}")]
    [Authorize(Policy = Permissions.ConfigEdit)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var entity = await db.Repositories.FindAsync([id], ct);
        if (entity is null) return NotFound();

        if (await db.MergeRequests.AnyAsync(mr => mr.RepositoryId == id, ct))
            return Conflict(new { error = localizer["Repo_HasMergeRequests"].Value });

        db.Repositories.Remove(entity);
        await db.SaveChangesAsync(ct);
        return NoContent();
    }
}

/// <summary>A repository registration. Used for both create and update.</summary>
/// <param name="Name">Display name, this service's own.</param>
/// <param name="ExternalId">
/// How the provider identifies it - GitLab wants the full <c>group/project</c> path, and a bare name
/// is the misconfiguration that makes every poll of it 404.
/// </param>
/// <param name="ConnectionId">The VCS connection that reaches it.</param>
/// <param name="TrackerConnectionId">
/// Which tracker this repository's branch issue keys belong to. Null falls back to matching the
/// key across every tracker, which is fine with one tracker and ambiguous with several.
/// </param>
public record CreateRepositoryRequest(
    [Required, MaxLength(300)] string Name,
    [Required, MaxLength(500)] string ExternalId,
    [Required] Guid ConnectionId,
    Guid? TrackerConnectionId = null);
