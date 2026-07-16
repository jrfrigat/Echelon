using System.ComponentModel.DataAnnotations;
using MassTransit;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ReleaseOrchestrator.Application.Contracts.Messages;
using ReleaseOrchestrator.Core.Entities;
using ReleaseOrchestrator.Core.Enums;
using ReleaseOrchestrator.Infrastructure.Auth;
using ReleaseOrchestrator.Infrastructure.Persistence;

namespace ReleaseOrchestrator.Web.Controllers;

[ApiController]
[Route("api/stacks")]
[Authorize(Policy = Permissions.ReleasePlanView)]
public class StacksController(
    AppDbContext db,
    IPublishEndpoint publisher,
    TimeProvider clock) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] int page = 1, [FromQuery] int pageSize = Paging.DefaultPageSize, CancellationToken ct = default)
    {
        var paging = Paging.From(page, pageSize);
        var total = await db.Stacks.CountAsync(ct);

        var items = await db.Stacks
            .OrderBy(s => s.Name).ThenBy(s => s.Id)
            .Select(s => new { s.Id, s.Name })
            .Skip(paging.Skip).Take(paging.PageSize)
            .ToListAsync(ct);

        return Ok(new { Total = total, Page = paging.Page, PageSize = paging.PageSize, Items = items });
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var stack = await db.Stacks
            .Where(s => s.Id == id)
            .Select(s => new
            {
                s.Id,
                s.Name,
                DependsOn = s.DependentOn.Select(d => new { d.Id, d.ToStackId, ToStackName = d.ToStack.Name, Type = d.Type.ToString() }),
                Repositories = s.RepositoryStacks.Select(rs => new { rs.RepositoryId, rs.Repository.Name })
            })
            .AsSplitQuery()
            .FirstOrDefaultAsync(ct);

        return stack is null ? NotFound() : Ok(stack);
    }

    [HttpPost]
    [Authorize(Policy = Permissions.ConfigEdit)]
    public async Task<IActionResult> Create([FromBody] CreateStackRequest req, CancellationToken ct)
    {
        if (await db.Stacks.AnyAsync(s => s.Name == req.Name, ct))
            return Conflict(new { error = $"A stack named '{req.Name}' already exists." });

        var stack = new Stack { Id = Guid.NewGuid(), Name = req.Name };
        db.Stacks.Add(stack);
        await db.SaveChangesAsync(ct);
        return CreatedAtAction(nameof(Get), new { id = stack.Id }, new { stack.Id, stack.Name });
    }

    [HttpPost("{id:guid}/dependencies")]
    [Authorize(Policy = Permissions.ConfigEdit)]
    public async Task<IActionResult> AddDependency(Guid id, [FromBody] AddStackDependencyRequest req, CancellationToken ct)
    {
        if (!Enum.TryParse<StackDependencyType>(req.Type, true, out var type))
            return BadRequest(new { error = $"Unknown type '{req.Type}'. Valid: {string.Join(", ", Enum.GetNames<StackDependencyType>())}" });

        if (id == req.ToStackId)
            return BadRequest(new { error = "A stack cannot depend on itself." });

        if (!await db.Stacks.AnyAsync(s => s.Id == id, ct))
            return NotFound(new { error = $"Stack {id} not found." });

        if (!await db.Stacks.AnyAsync(s => s.Id == req.ToStackId, ct))
            return BadRequest(new { error = $"Stack {req.ToStackId} not found." });

        if (await db.StackDependencies.AnyAsync(d => d.FromStackId == id && d.ToStackId == req.ToStackId, ct))
            return Conflict(new { error = "That dependency already exists." });

        db.StackDependencies.Add(new StackDependency
        {
            Id = Guid.NewGuid(),
            FromStackId = id,
            ToStackId = req.ToStackId,
            Type = type
        });
        await db.SaveChangesAsync(ct);

        await RequestRecalculationAsync($"Stack dependency added to {id}", ct);
        return NoContent();
    }

    [HttpDelete("{id:guid}/dependencies/{dependencyId:guid}")]
    [Authorize(Policy = Permissions.ConfigEdit)]
    public async Task<IActionResult> RemoveDependency(Guid id, Guid dependencyId, CancellationToken ct)
    {
        var dep = await db.StackDependencies.FirstOrDefaultAsync(d => d.Id == dependencyId && d.FromStackId == id, ct);
        if (dep is null) return NotFound();

        db.StackDependencies.Remove(dep);
        await db.SaveChangesAsync(ct);

        await RequestRecalculationAsync($"Stack dependency removed from {id}", ct);
        return NoContent();
    }

    [HttpPost("{id:guid}/repositories")]
    [Authorize(Policy = Permissions.ConfigEdit)]
    public async Task<IActionResult> AssignRepository(Guid id, [FromBody] AssignRepositoryRequest req, CancellationToken ct)
    {
        if (!await db.Stacks.AnyAsync(s => s.Id == id, ct))
            return NotFound(new { error = $"Stack {id} not found." });

        if (!await db.Repositories.AnyAsync(r => r.Id == req.RepositoryId, ct))
            return BadRequest(new { error = $"Repository {req.RepositoryId} not found." });

        if (await db.RepositoryStacks.AnyAsync(rs => rs.StackId == id && rs.RepositoryId == req.RepositoryId, ct))
            return Conflict(new { error = "That repository is already in the stack." });

        db.RepositoryStacks.Add(new RepositoryStack { StackId = id, RepositoryId = req.RepositoryId });
        await db.SaveChangesAsync(ct);

        await RequestRecalculationAsync($"Repository assigned to stack {id}", ct);
        return NoContent();
    }

    [HttpDelete("{id:guid}/repositories/{repositoryId:guid}")]
    [Authorize(Policy = Permissions.ConfigEdit)]
    public async Task<IActionResult> UnassignRepository(Guid id, Guid repositoryId, CancellationToken ct)
    {
        var link = await db.RepositoryStacks.FirstOrDefaultAsync(rs => rs.StackId == id && rs.RepositoryId == repositoryId, ct);
        if (link is null) return NotFound();

        db.RepositoryStacks.Remove(link);
        await db.SaveChangesAsync(ct);

        await RequestRecalculationAsync($"Repository removed from stack {id}", ct);
        return NoContent();
    }

    /// <summary>
    /// Stack topology decides deploy order, so changing it invalidates the plan. README §5
    /// calls for a rebuild on this; nothing used to trigger one.
    /// </summary>
    private Task RequestRecalculationAsync(string reason, CancellationToken ct) =>
        publisher.Publish(new ReleasePlanRecalculationRequested(clock.GetUtcNow().UtcDateTime, reason), ct);
}

public record CreateStackRequest([property: Required, MaxLength(200)] string Name);
public record AddStackDependencyRequest(Guid ToStackId, [property: Required] string Type);
public record AssignRepositoryRequest(Guid RepositoryId);
