using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Rebus.Bus;
using ReleaseOrchestrator.Application.Services;
using ReleaseOrchestrator.Application.Contracts.Messages;
using ReleaseOrchestrator.Core.Parsing;
using ReleaseOrchestrator.Infrastructure.Persistence;

namespace ReleaseOrchestrator.Infrastructure.Queue;

public class TaskReconciliationOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>How often open tasks are re-read from their tracker.</summary>
    public int IntervalMinutes { get; set; } = 30;

    /// <summary>Cap per pass, so a large backlog cannot flood the tracker's API.</summary>
    public int MaxTasksPerRun { get; set; } = 500;
}

/// <summary>
/// Periodically asks for open tasks to be re-read from their tracker.
///
/// Dependency links are what order the plan, and trackers do not raise an event when a link is
/// added or removed — a status webhook only says the status changed. So an edge added in the
/// tracker would never reach us until something unrelated happened to touch that task. Event
/// handling covers the common path; this covers the rest.
/// </summary>
/// <remarks>
/// Registered in every replica but gated on a lease, so one pass runs per interval across the
/// deployment rather than one per replica — the tracker's API is the scarce resource here, and N
/// replicas asking it about the same tasks N times bought nothing.
/// </remarks>
public class TaskReconciliationService(
    IServiceScopeFactory scopeFactory,
    IOptions<TaskReconciliationOptions> options,
    IDistributedLease lease,
    TimeProvider clock,
    ILogger<TaskReconciliationService> logger) : BackgroundService
{
    /// <summary>Lease name; shared by every replica of this service.</summary>
    private const string LeaseName = "task-reconciliation";
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled)
        {
            logger.LogInformation("Task reconciliation is disabled");
            return;
        }

        var interval = TimeSpan.FromMinutes(Math.Max(options.Value.IntervalMinutes, 1));

        // Wait one interval before the first pass: at startup the event path has not had a chance
        // to run, and racing it just doubles the calls.
        using var timer = new PeriodicTimer(interval, clock);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await timer.WaitForNextTickAsync(stoppingToken)) return;

                // Held for the interval, not for the expected duration of the pass: a replica that
                // dies mid-pass then blocks the job for at most one cycle, and a slow pass renews.
                await using var held = await lease.TryAcquireAsync(LeaseName, interval, stoppingToken);
                if (held is null)
                {
                    logger.LogDebug("Another replica is reconciling; skipping this pass");
                    continue;
                }

                await ReconcileAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // One failed pass must not kill the service: the next tick retries.
                logger.LogError(ex, "Task reconciliation pass failed");
            }
        }
    }

    private async Task ReconcileAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var bus = scope.ServiceProvider.GetRequiredService<IBus>();

        // Only open tasks: a closed task's links can no longer change the plan, and re-reading
        // every task ever imported would grow without bound.
        var stale = await db.Tasks
            .Where(t => t.ClosedAt == null)
            .OrderBy(t => t.Id)
            .Take(options.Value.MaxTasksPerRun)
            .Select(t => new { t.ExternalId, TrackerName = t.TrackerConnection.Name })
            .AsNoTracking()
            .ToListAsync(ct);

        if (stale.Count == 0) return;

        foreach (var task in stale)
            await bus.Send(
                new TaskSyncRequested(task.TrackerName, task.ExternalId, "Periodic reconciliation"));

        logger.LogInformation("Requested reconciliation of {Count} open task(s)", stale.Count);

        if (stale.Count == options.Value.MaxTasksPerRun)
            logger.LogWarning(
                "Reconciliation hit its cap of {Cap} tasks; the remainder waits for the next pass. "
                + "Raise TaskReconciliation__MaxTasksPerRun if open tasks consistently exceed it.",
                options.Value.MaxTasksPerRun);
    }
}
