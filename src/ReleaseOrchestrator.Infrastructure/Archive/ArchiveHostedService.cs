using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ReleaseOrchestrator.Infrastructure.Persistence;

namespace ReleaseOrchestrator.Infrastructure.Archive;

/// <summary>
/// Nightly archiving of superseded release plans, closed merge requests and closed tasks.
/// </summary>
/// <remarks>
/// <para>
/// LIMITATION — this service has no leader election. It is registered unconditionally, so
/// every replica runs the cycle at the same UTC hour against the same rows. Concurrent runs
/// select overlapping batches and compete for the same deletes: expect deadlock victims and
/// duplicated work under more than one replica. Correctness survives it — the archive insert
/// skips rows another replica already wrote, and a batch lost to a deadlock is retried and
/// then left for the next cycle — but the cycle is slower and noisier than it should be.
/// </para>
/// <para>
/// Run a single replica, or set <c>Archiving__Enabled=false</c> on all but one, until a
/// distributed lock is available to gate this properly.
/// </para>
/// </remarks>
public class ArchiveHostedService(
    IServiceScopeFactory scopeFactory,
    IOptions<ArchiveOptions> options,
    TimeProvider clock,
    ILogger<ArchiveHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled)
        {
            logger.LogInformation("Archiving is disabled");
            return;
        }

        var runAtHour = ResolveRunAtHour();

        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = DelayUntilNextRun(runAtHour);
            logger.LogInformation("Next archive cycle in {Delay} (at {RunAtHour:00}:00 UTC)", delay, runAtHour);

            try
            {
                await Task.Delay(delay, stoppingToken);
                await RunArchiveCycleAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    private int ResolveRunAtHour()
    {
        var configured = options.Value.RunAtUtcHour;
        var hour = Math.Clamp(configured, 0, 23);
        if (hour != configured)
        {
            logger.LogWarning("Archiving:RunAtUtcHour is {Configured}, which is not an hour of day; using {Hour}",
                configured, hour);
        }

        return hour;
    }

    private TimeSpan DelayUntilNextRun(int runAtHour)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var nextRun = now.Date.AddHours(runAtHour);

        // Only roll to tomorrow once today's slot has passed. Unconditionally adding a day
        // meant a process started at 01:00 waited 25 hours for its first 02:00 run.
        if (nextRun <= now) nextRun = nextRun.AddDays(1);

        return nextRun - now;
    }

    private async Task RunArchiveCycleAsync(CancellationToken ct)
    {
        logger.LogInformation("Archive cycle started");

        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var archiveDb = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();
        var runner = new ArchiveRunner(db, archiveDb, options.Value, clock, logger);

        var cutoff = clock.GetUtcNow().UtcDateTime.AddDays(-options.Value.ArchiveAfterDays);

        // The order is load-bearing, in both directions of the foreign keys:
        //  - plans first, because their StageItems reference merge requests with Restrict, so
        //    an MR that ever entered any plan stays undeletable while that plan exists;
        //  - merge requests before tasks, because MergeRequest.TaskId is SetNull, so deleting
        //    a task first blanks the link and the archived MR loses its TaskExternalId.
        await RunPhaseAsync("release plans", c => runner.ArchiveReleasePlansAsync(cutoff, c), ct);
        await RunPhaseAsync("merge requests", c => runner.ArchiveMergeRequestsAsync(cutoff, c), ct);
        await RunPhaseAsync("tasks", c => runner.ArchiveTasksAsync(cutoff, c), ct);

        logger.LogInformation("Archive cycle completed");
    }

    // One phase failing must not cancel the others: a single try/catch around the whole cycle
    // meant the first error while archiving tasks also skipped merge requests for that night.
    private async Task RunPhaseAsync(string phase, Func<CancellationToken, Task> work, CancellationToken ct)
    {
        try
        {
            await work(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Archive phase {Phase} failed; continuing with the remaining phases", phase);
        }
    }
}
