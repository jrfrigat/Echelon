using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using ReleaseOrchestrator.Core.Enums;
using ReleaseOrchestrator.Infrastructure.Persistence;

namespace ReleaseOrchestrator.Web.Controllers;

[ApiController]
[Route("api/merge-requests")]
[Authorize]
public class MergeRequestsController(AppDbContext db) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] string? status,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken ct = default)
    {
        var query = db.MergeRequests
            .Include(mr => mr.Repository).ThenInclude(r => r.Connection)
            .Include(mr => mr.Task)
            .AsQueryable();

        if (!string.IsNullOrEmpty(status) && Enum.TryParse<MergeRequestStatus>(status, true, out var s))
            query = query.Where(mr => mr.Status == s);

        var total = await query.CountAsync(ct);
        var items = await query
            .OrderByDescending(mr => mr.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(mr => new {
                mr.Id, mr.ExternalId, mr.SourceBranch, mr.TargetBranch,
                mr.Status, mr.CreatedAt, mr.MergedAt,
                RepositoryId = mr.RepositoryId,
                RepositoryName = mr.Repository.Name,
                ConnectionName = mr.Repository.Connection.Name,
                TaskExternalId = mr.Task != null ? mr.Task.ExternalId : null
            })
            .ToListAsync(ct);

        return Ok(new { Total = total, Page = page, PageSize = pageSize, Items = items });
    }
}
