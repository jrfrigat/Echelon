using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using ReleaseOrchestrator.Application.DTOs;
using ReleaseOrchestrator.Infrastructure.Archive;
using ReleaseOrchestrator.Infrastructure.Audit;
using ReleaseOrchestrator.Infrastructure.Auth;

namespace ReleaseOrchestrator.Web.Controllers;

/// <summary>
/// Reads the HTTP request audit: which endpoints are being called, which are failing, and how slow
/// they are.
/// </summary>
/// <remarks>
/// <para>
/// Guarded by <c>ConfigEdit</c>, not the view policy every signed-in user holds. These rows carry
/// names, addresses and activity for everyone — that is administrator data, and the one existing
/// admin-only controller uses the same policy.
/// </para>
/// <para>
/// Rate limiting is disabled here for the same reason it is on the metrics endpoint: a diagnostics
/// screen must not spend the request budget of the traffic it is diagnosing, least of all while
/// somebody is trying to work out why that traffic is failing.
/// </para>
/// </remarks>
[ApiController]
[Route("api/request-audit")]
[Authorize(Policy = Permissions.ConfigEdit)]
[DisableRateLimiting]
public class RequestAuditController(
    ArchiveDbContext db,
    RequestAuditBuffer buffer,
    IOptions<RequestAuditOptions> options) : ControllerBase
{
    /// <summary>Recorded requests, newest first.</summary>
    /// <param name="minutes">How far back to look.</param>
    /// <param name="status">Optional status class: <c>error</c>, <c>client</c>, <c>server</c>.</param>
    /// <param name="notableOnly">Failures and state changes only. On by default — it is what an operator wants.</param>
    /// <param name="includeAuditTraffic">
    /// Include this screen's own reads. Off by default so the page is not mostly itself, but
    /// available: an audit that hides what the auditor did is the wrong artifact.
    /// </param>
    /// <param name="search">Free text matched against the path and route pattern.</param>
    /// <param name="page">1-based page.</param>
    /// <param name="pageSize">Page size, clamped by <see cref="Paging"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] int minutes = 60,
        [FromQuery] string? status = null,
        [FromQuery] bool notableOnly = true,
        [FromQuery] bool includeAuditTraffic = false,
        [FromQuery] string? search = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = Paging.DefaultPageSize,
        CancellationToken ct = default)
    {
        var paging = Paging.From(page, pageSize);
        var query = Filtered(minutes, status, notableOnly, includeAuditTraffic, search);

        var total = await query.CountAsync(ct);

        var items = await query
            // The Id tiebreaker is not optional: without it, rows sharing a StartedAt can repeat on
            // one page and vanish from the next.
            .OrderByDescending(e => e.StartedAt).ThenByDescending(e => e.Id)
            .Skip(paging.Skip).Take(paging.PageSize)
            .Select(e => new RequestAuditEntryDto(
                e.Id, e.Method, e.RoutePattern, e.Path, e.Kind, e.StatusCode, e.DurationMs,
                e.StartedAt, e.UserId, e.UserName, e.PeerIp, e.ForwardedIp, e.Permission,
                e.CorrelationId, e.ExceptionType, e.Instance))
            .AsNoTracking()
            .ToListAsync(ct);

        return Ok(new { Total = total, paging.Page, paging.PageSize, Items = items });
    }

    /// <summary>Traffic, failures and latency per endpoint over the window.</summary>
    /// <param name="minutes">How far back to look.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <remarks>
    /// Percentiles are computed from a capped in-memory sample rather than in SQL. SQL Server's
    /// <c>PERCENTILE_CONT</c> is a window function with no GROUP BY form while PostgreSQL's is an
    /// ordered-set aggregate, so doing it in the database means one hand-written query per provider —
    /// and this codebase keeps provider divergence to the two documented cases. When the cap binds,
    /// the answer says so instead of quietly rounding.
    /// </remarks>
    [HttpGet("summary")]
    public async Task<IActionResult> Summary([FromQuery] int minutes = 60, CancellationToken ct = default)
    {
        var window = Math.Clamp(minutes, 1, 10_080);
        var from = DateTime.UtcNow.AddMinutes(-window);
        var to = DateTime.UtcNow;

        var sampleCap = Math.Max(1_000, options.Value.SummarySampleCap);

        var sample = await db.RequestAuditEntries
            .Where(e => e.StartedAt >= from)
            .OrderByDescending(e => e.StartedAt)
            .Take(sampleCap)
            .Select(e => new { e.Method, e.RoutePattern, e.StatusCode, e.DurationMs })
            .AsNoTracking()
            .ToListAsync(ct);

        var total = await db.RequestAuditEntries.LongCountAsync(e => e.StartedAt >= from, ct);
        var errors = await db.RequestAuditEntries.LongCountAsync(e => e.StartedAt >= from && e.StatusCode >= 400, ct);

        var routes = sample
            .GroupBy(e => new { e.Method, e.RoutePattern })
            .Select(g =>
            {
                var durations = g.Select(x => x.DurationMs).OrderBy(d => d).ToList();
                return new RequestAuditRouteStatDto(
                    g.Key.Method,
                    g.Key.RoutePattern,
                    g.LongCount(),
                    g.LongCount(x => x.StatusCode is >= 400 and < 500),
                    g.LongCount(x => x.StatusCode >= 500),
                    Percentile(durations, 0.50),
                    Percentile(durations, 0.95),
                    durations.Count == 0 ? 0 : durations[^1]);
            })
            // Worst first: the first question is which endpoint is failing, not what the global rate is.
            .OrderByDescending(r => r.ServerErrors)
            .ThenByDescending(r => r.ClientErrors)
            .ThenByDescending(r => r.Count)
            .Take(50)
            .ToList();

        return Ok(new RequestAuditSummaryDto(
            from, to, total, errors, routes,
            PercentilesApproximate: sample.Count >= sampleCap,
            DroppedRecords: buffer.Dropped,
            AnonymousCapBound: buffer.AnonymousCapBound,
            // Stated as data rather than left to be discovered: the webhook host is a separate
            // process and does not record here.
            IngressCovered: false));
    }

    private IQueryable<ReleaseOrchestrator.Infrastructure.Archive.Entities.RequestAuditEntry> Filtered(
        int minutes, string? status, bool notableOnly, bool includeAuditTraffic, string? search)
    {
        var from = DateTime.UtcNow.AddMinutes(-Math.Clamp(minutes, 1, 10_080));
        var query = db.RequestAuditEntries.Where(e => e.StartedAt >= from);

        if (notableOnly) query = query.Where(e => e.IsNotable);

        query = status switch
        {
            "error" => query.Where(e => e.StatusCode >= 400),
            "client" => query.Where(e => e.StatusCode >= 400 && e.StatusCode < 500),
            "server" => query.Where(e => e.StatusCode >= 500),
            _ => query
        };

        // Filtered out of the default view rather than never recorded, so "who has been reading
        // everyone's activity" stays answerable.
        if (!includeAuditTraffic)
            query = query.Where(e => !e.RoutePattern.StartsWith("api/request-audit"));

        if (!string.IsNullOrWhiteSpace(search))
            query = query.Where(e => e.Path.Contains(search) || e.RoutePattern.Contains(search));

        return query;
    }

    /// <summary>Nearest-rank percentile over a pre-sorted list.</summary>
    private static int Percentile(List<int> sorted, double fraction)
    {
        if (sorted.Count == 0) return 0;
        var index = (int)Math.Ceiling(fraction * sorted.Count) - 1;
        return sorted[Math.Clamp(index, 0, sorted.Count - 1)];
    }
}
