using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ReleaseOrchestrator.Application.Services;
using ReleaseOrchestrator.Infrastructure.Persistence;

namespace ReleaseOrchestrator.Infrastructure.Archive;

/// <summary>
/// Nightly archiving of closed merge requests and closed tasks.
/// </summary>
/// <remarks>
/// Registered in every replica but gated on a lease, so one cycle runs per night across the
/// deployment. Without it every replica woke at the same UTC hour and raced for the same deletes:
/// correctness held — the archive insert skips rows another replica already wrote — but they
/// deadlocked each other doing work only one of them needed to do.
/// </remarks>
public class ArchiveHostedService(
    IServiceScopeFactory scopeFactory,
    IOptions<ArchiveOptions> options,
    IDistributedLease lease,
    TimeProvider clock,
    ILogger<ArchiveHostedService> logger) : BackgroundService
{
    /// <summary>Lease name; shared by every replica of this service.</summary>
    private const string LeaseName = "archive-cycle";

    /// <summary>
    /// How long the lease survives without renewal. Generous because the cycle is long and runs
    /// once a night: the cost of over-holding is a skipped night, the cost of under-holding is two
    /// replicas deleting the same rows.
    /// </summary>
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(30);
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

                await using var held = await lease.TryAcquireAsync(LeaseName, LeaseDuration, stoppingToken);
                if (held is null)
                {
                    logger.LogInformation("Another replica is running the archive cycle; skipping tonight's run");
                    continue;
                }

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

        // Merge requests before tasks: MergeRequest.TaskId is SetNull, so deleting a task first
        // blanks the link and the archived merge request loses its TaskExternalId. (A merge request
        // referenced by a live plan, rollout or deployment state is skipped by the merge-request
        // phase itself -- those references are Restrict -- so no plan phase has to run first.)
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
