using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ReleaseOrchestrator.Core.Entities;
using ReleaseOrchestrator.Core.Enums;
using ReleaseOrchestrator.Infrastructure.Auth;
using ReleaseOrchestrator.Infrastructure.Persistence;

namespace ReleaseOrchestrator.Web.Controllers;

[ApiController]
[Route("api/stacks")]
[Authorize]
public class StacksController(AppDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] int page = 1, [FromQuery] int pageSize = 50, CancellationToken ct = default)
    {
        var total = await db.Stacks.CountAsync(ct);
        var items = await db.Stacks
            .Select(s => new { s.Id, s.Name })
            .Skip((page - 1) * pageSize).Take(pageSize)
            .ToListAsync(ct);
        return Ok(new { Total = total, Page = page, PageSize = pageSize, Items = items });
    }

    [HttpPost]
    [Authorize(Policy = Permissions.ConfigEdit)]
    public async Task<IActionResult> Create([FromBody] CreateStackRequest req, CancellationToken ct)
    {
        var stack = new Stack { Id = Guid.NewGuid(), Name = req.Name };
        db.Stacks.Add(stack);
        await db.SaveChangesAsync(ct);
        return Created($"/api/stacks/{stack.Id}", new { stack.Id, stack.Name });
    }

    [HttpPost("{id:guid}/dependencies")]
    [Authorize(Policy = Permissions.ConfigEdit)]
    public async Task<IActionResult> AddDependency(Guid id, [FromBody] AddStackDependencyRequest req, CancellationToken ct)
    {
        db.StackDependencies.Add(new StackDependency
        {
            Id = Guid.NewGuid(),
            FromStackId = id,
            ToStackId = req.ToStackId,
            Type = Enum.Parse<StackDependencyType>(req.Type)
        });
        await db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpPost("{id:guid}/repositories")]
    [Authorize(Policy = Permissions.ConfigEdit)]
    public async Task<IActionResult> AssignRepository(Guid id, [FromBody] AssignRepositoryRequest req, CancellationToken ct)
    {
        var exists = await db.RepositoryStacks.AnyAsync(rs => rs.StackId == id && rs.RepositoryId == req.RepositoryId, ct);
        if (exists) return Conflict();
        db.RepositoryStacks.Add(new RepositoryStack { StackId = id, RepositoryId = req.RepositoryId });
        await db.SaveChangesAsync(ct);
        return NoContent();
    }
}

public record CreateStackRequest(string Name);
public record AddStackDependencyRequest(Guid ToStackId, string Type);
public record AssignRepositoryRequest(Guid RepositoryId);
