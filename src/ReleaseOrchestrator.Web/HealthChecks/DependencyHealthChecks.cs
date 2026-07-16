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

public class RedisHealthCheck(IDistributedCache cache) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken ct = default)
    {
        try
        {
            await cache.GetAsync("health:probe", ct);
            return HealthCheckResult.Healthy();
        }
        catch (Exception ex)
        {
            // Permission claims fail closed when Redis is down, so every request 403s.
            return HealthCheckResult.Unhealthy("Redis is unreachable; authorization would fail closed.", ex);
        }
    }
}
