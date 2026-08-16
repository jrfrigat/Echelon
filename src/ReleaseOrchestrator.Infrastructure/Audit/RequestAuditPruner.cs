using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ReleaseOrchestrator.Application.Services;
using ReleaseOrchestrator.Infrastructure.Archive;

namespace ReleaseOrchestrator.Infrastructure.Audit;

/// <summary>
/// Enforces the audit's retention window: ordinary traffic goes early, failures and state changes
/// are kept far longer.
/// </summary>
/// <remarks>
/// <para>
/// Delete-only, unlike the archive phases beside it. These rows are read by one screen and by
/// nothing else, so copying them into a third database on their way out would preserve data no tool
/// can display.
/// </para>
/// <para>
/// Leased, because unlike the writer this IS shared work: every replica would otherwise delete the
/// same rows. But the lease is a load control, not a correctness primitive, which is what makes the
/// escape hatch below safe.
/// </para>
/// </remarks>
public class RequestAuditPruner(
    IServiceScopeFactory scopeFactory,
    IDistributedLease lease,
    IOptions<RequestAuditOptions> options,
    TimeProvider clock,
    ILogger<RequestAuditPruner> logger) : BackgroundService
{
    private const string LeaseName = "request-audit-prune";

    /// <summary>Consecutive passes that could not take the lease. Feeds the hard-cap escape hatch.</summary>
    private int _leaseFailures;

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;

        if (!settings.Enabled)
        {
            logger.LogInformation("Request audit pruner is disabled.");
            return;
        }

        var interval = TimeSpan.FromMinutes(Math.Max(1, settings.PruneIntervalMinutes));
        using var timer = new PeriodicTimer(interval, clock);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await timer.WaitForNextTickAsync(stoppingToken);
                await RunPassAsync(settings, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Request audit prune pass failed");
            }
        }
    }

    private async Task RunPassAsync(RequestAuditOptions settings, CancellationToken ct)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();

        await using var held = await lease.TryAcquireAsync(LeaseName, TimeSpan.FromMinutes(10), ct);

        if (held is null)
        {
            _leaseFailures++;

            // The lease fails closed when its backend is unreachable, so a long outage would stop
            // pruning while the writer keeps inserting -- and the table would grow without limit
            // precisely when nobody is watching. Past the hard cap, prune anyway: the deletes are
            // idempotent and batched, so two replicas doing it is wasteful but harmless, whereas an
            // unbounded table is not.
            var rows = await db.RequestAuditEntries.LongCountAsync(ct);
            if (_leaseFailures < 3 || rows <= settings.HardRowCap) return;

            logger.LogWarning(
                "Request audit prune lease unavailable for {Failures} passes and the table holds {Rows} rows "
                + "(cap {Cap}); pruning without the lease.",
                _leaseFailures, rows, settings.HardRowCap);
        }
        else
        {
            _leaseFailures = 0;
        }

        var now = clock.GetUtcNow().UtcDateTime;
        var ordinaryCutoff = now.AddDays(-Math.Max(1, settings.RetainDays));
        var notableCutoff = now.AddDays(-Math.Max(1, settings.RetainNotableDays));

        var removed = await DeleteAsync(db, e => !e.IsNotable && e.StartedAt < ordinaryCutoff, settings, ct);
        removed += await DeleteAsync(db, e => e.IsNotable && e.StartedAt < notableCutoff, settings, ct);

        if (removed > 0)
            logger.LogInformation("Pruned {Count} request audit row(s)", removed);
    }

    /// <summary>
    /// Deletes matching rows in batches.
    /// </summary>
    /// <remarks>
    /// Batched rather than one statement: a single delete covering an hourly slice of a
    /// high-volume table is how a delete becomes a lock problem. Bounded by a pass limit so one
    /// cycle cannot run indefinitely - whatever is left goes next hour.
    /// </remarks>
    private static async Task<int> DeleteAsync(
        ArchiveDbContext db,
        System.Linq.Expressions.Expression<Func<Archive.Entities.RequestAuditEntry, bool>> predicate,
        RequestAuditOptions settings,
        CancellationToken ct)
    {
        var batchSize = Math.Max(100, settings.PruneBatchSize);
        var total = 0;

        for (var pass = 0; pass < 20; pass++)
        {
            var ids = await db.RequestAuditEntries
                .Where(predicate)
                .OrderBy(e => e.Id)
                .Take(batchSize)
                .Select(e => e.Id)
                .ToListAsync(ct);

            if (ids.Count == 0) break;

            total += await db.RequestAuditEntries.Where(e => ids.Contains(e.Id)).ExecuteDeleteAsync(ct);

            if (ids.Count < batchSize) break;
            await Task.Delay(TimeSpan.FromSeconds(1), ct);
        }

        return total;
    }
}
