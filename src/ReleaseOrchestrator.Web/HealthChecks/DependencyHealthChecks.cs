using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using ReleaseOrchestrator.Infrastructure.Archive;
using ReleaseOrchestrator.Infrastructure.Persistence;

namespace ReleaseOrchestrator.Web.HealthChecks;

/// <summary>
/// Readiness probes for the hard dependencies. The previous /health returned a constant
/// "healthy", so an orchestrator kept routing traffic to an instance whose database was
/// unreachable.
/// </summary>
public class DatabaseHealthCheck(AppDbContext db) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        try
        {
            return await db.Database.CanConnectAsync(ct)
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Unhealthy("Cannot connect to the operational database.");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy("Operational database check failed.", ex);
        }
    }
}

public class ArchiveDatabaseHealthCheck(ArchiveDbContext db) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        try
        {
            // Degraded, not unhealthy: archiving is a background concern, and taking the
            // instance out of rotation over it would be worse than the outage itself.
            return await db.Database.CanConnectAsync(ct)
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Degraded("Cannot connect to the archive database.");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Degraded("Archive database check failed.", ex);
        }
    }
}

/// <summary>
/// Probes whatever backs the permission cache, whichever backend that is.
/// </summary>
/// <remarks>
/// Named for the port rather than for Redis: the backend is configuration
/// (<c>Coordination:Provider</c>), and a check called "redis" would be a lie on a single-instance
/// deployment that has no Redis. Under <c>memory</c> it is a process-local dictionary and this
/// passes trivially — which is the honest answer, since a cache inside the process is reachable
/// exactly when the process is.
/// </remarks>
public class CoordinationHealthCheck(IDistributedCache cache) : IHealthCheck
{
    /// <inheritdoc/>
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        try
        {
            await cache.GetAsync("health:probe", ct);
            return HealthCheckResult.Healthy();
        }
        catch (Exception ex)
        {
            // Permission claims fail closed when the cache is unreachable, so every request 403s.
            return HealthCheckResult.Unhealthy(
                "The coordination cache is unreachable; authorization would fail closed.", ex);
        }
    }
}
