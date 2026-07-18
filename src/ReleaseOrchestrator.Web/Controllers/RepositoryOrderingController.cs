using System.ComponentModel.DataAnnotations;
using Rebus.Bus;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ReleaseOrchestrator.Application.Contracts.Messages;
using ReleaseOrchestrator.Core.Enums;
using ReleaseOrchestrator.Infrastructure.Auth;
using ReleaseOrchestrator.Infrastructure.Persistence;
using ReleaseOrchestrator.Infrastructure.Persistence.Models;

namespace ReleaseOrchestrator.Web.Controllers;

/// <summary>
/// Repository-ordering rules -- "repository A deploys after repository B", Hard or Soft. This is
/// the editable, provider-neutral replacement for stacks: the plan-building configuration an
/// operator adjusts so a task's merge requests deploy in the right order across repositories.
/// </summary>
/// <remarks>
/// Error messages here are not localized yet; localization is folded in with the rest of the PWA
/// in the finalize phase (docs/issues/009-admin-and-migration.md).
/// </remarks>
[ApiController]
[Route("api/repository-ordering")]
[Authorize(Policy = Permissions.ReleasePlanView)]
public class RepositoryOrderingController(AppDbContext db, IBus bus, TimeProvider clock) : ControllerBase
{
    /// <summary>Lists the repository-ordering rules.</summary>
    /// <param name="page">1-based page number.</param>
    /// <param name="pageSize">Page size, clamped by <see cref="Paging"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] int page = 1, [FromQuery] int pageSize = Paging.DefaultPageSize, CancellationToken ct = default)
    {
        var paging = Paging.From(page, pageSize);
        var total = await db.RepositoryDependencies.CountAsync(ct);

        var items = await db.RepositoryDependencies
            .OrderBy(d => d.FromRepository.Name).ThenBy(d => d.Id)
            .Select(d => new
            {
                d.Id,
                d.FromRepositoryId,
                FromRepositoryName = d.FromRepository.Name,
                d.ToRepositoryId,
                ToRepositoryName = d.ToRepository.Name,
                Type = d.Type.ToString()
            })
            .Skip(paging.Skip).Take(paging.PageSize)
            .ToListAsync(ct);

        return Ok(new { Total = total, paging.Page, paging.PageSize, Items = items });
    }

    /// <summary>Adds a rule "<paramref name="req"/>.From deploys after <paramref name="req"/>.To".</summary>
    /// <param name="req">The rule to add.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpPost]
    [Authorize(Policy = Permissions.ConfigEdit)]
    public async Task<IActionResult> Create([FromBody] CreateRepositoryDependencyRequest req, CancellationToken ct)
    {
        if (!Enum.TryParse<StackDependencyType>(req.Type, ignoreCase: true, out var type))
            return BadRequest(new { error = $"Unknown dependency type '{req.Type}'. Expected one of: {string.Join(", ", Enum.GetNames<StackDependencyType>())}." });

        if (req.FromRepositoryId == req.ToRepositoryId)
            return BadRequest(new { error = "A repository cannot depend on itself." });

        if (!await db.Repositories.AnyAsync(r => r.Id == req.FromRepositoryId, ct))
            return BadRequest(new { error = $"Repository {req.FromRepositoryId} not found." });

        if (!await db.Repositories.AnyAsync(r => r.Id == req.ToRepositoryId, ct))
            return BadRequest(new { error = $"Repository {req.ToRepositoryId} not found." });

        if (await db.RepositoryDependencies.AnyAsync(
                d => d.FromRepositoryId == req.FromRepositoryId && d.ToRepositoryId == req.ToRepositoryId, ct))
            return Conflict(new { error = "This repository dependency already exists." });

        var dep = new RepositoryDependency
        {
            Id = Guid.NewGuid(),
            FromRepositoryId = req.FromRepositoryId,
            ToRepositoryId = req.ToRepositoryId,
            Type = type
        };
        db.RepositoryDependencies.Add(dep);
        await db.SaveChangesAsync(ct);

        await RequestRecalculationAsync($"Repository dependency added: {req.FromRepositoryId} after {req.ToRepositoryId}", ct);
        return Ok(new { dep.Id });
    }

    /// <summary>Removes a repository-ordering rule.</summary>
    /// <param name="id">The rule id.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpDelete("{id:guid}")]
    [Authorize(Policy = Permissions.ConfigEdit)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var dep = await db.RepositoryDependencies.FirstOrDefaultAsync(d => d.Id == id, ct);
        if (dep is null) return NotFound();

        db.RepositoryDependencies.Remove(dep);
        await db.SaveChangesAsync(ct);

        await RequestRecalculationAsync($"Repository dependency removed: {id}", ct);
        return NoContent();
    }

    /// <summary>
    /// Repository ordering decides deploy order, so changing it invalidates the plan and triggers a
    /// rebuild -- the same reason stack changes did.
    /// </summary>
    private Task RequestRecalculationAsync(string reason, CancellationToken ct) =>
        bus.Send(new ReleasePlanRecalculationRequested(clock.GetUtcNow().UtcDateTime, reason));
}

/// <summary>Request to add a repository-ordering rule.</summary>
/// <param name="FromRepositoryId">The repository that deploys later.</param>
/// <param name="ToRepositoryId">The repository that deploys first.</param>
/// <param name="Type">"Hard" or "Soft".</param>
public record CreateRepositoryDependencyRequest(
    Guid FromRepositoryId,
    Guid ToRepositoryId,
    [property: Required] string Type);
