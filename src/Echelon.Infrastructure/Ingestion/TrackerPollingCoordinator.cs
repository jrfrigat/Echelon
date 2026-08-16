using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Rebus.Bus;
using Echelon.Application.Contracts.Messages;
using Echelon.Application.Services;
using Echelon.Core.Enums;
using Echelon.Infrastructure.Persistence;
using Echelon.Providers.Abstractions;
using Echelon.Providers.Abstractions.Vcs;

namespace Echelon.Infrastructure.Ingestion;

/// <summary>Tunables for the tracker poller.</summary>
public class TrackerPollingOptions
{
    /// <summary>Whether the poller runs. Off in tests.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>How often the poller wakes and sweeps the poll-mode tracker connections, in seconds. The floor for a connection's own interval.</summary>
    public int IntervalSeconds { get; set; } = 60;

    /// <summary>Cap per connection per pass, so a large backlog cannot flood the tracker's API.</summary>
    public int MaxTasksPerRun { get; set; } = 500;
}

/// <summary>
/// Re-reads the open tasks of connections whose tracker provider type is registered
/// <see cref="IngestionMode.Poll"/>, on each connection's own interval - the pull half of the tracker
/// ingestion, symmetric with <see cref="VcsPollingCoordinator"/>.
/// </summary>
/// <remarks>
/// <para>
/// Additive to <c>TaskReconciliationService</c>, which keeps every connection's dependency links fresh
/// on its own (coarser) global timer regardless of type. This gives a poll-mode connection - one the
/// tracker cannot push webhooks to - a faster, per-connection cadence for the status of its open tasks.
/// Both enqueue <see cref="TaskSyncRequested"/>, and re-reading a task is idempotent, so the overlap is
/// harmless: a poll connection is simply re-read more often than the reconciliation floor.
/// </para>
/// <para>
/// Each connection is swept on its own interval - the <see cref="VcsPollSettings.IntervalKey"/> setting
/// the poll tracker type declares - with the global tick (<see cref="TrackerPollingOptions.IntervalSeconds"/>)
/// as the floor, since the poller cannot wake more often than it ticks. Leased, so one replica polls.
/// </para>
/// </remarks>
public class TrackerPollingCoordinator(
    IServiceScopeFactory scopeFactory,
    IDistributedLease lease,
    TimeProvider clock,
    IOptions<TrackerPollingOptions> options,
    IEnumerable<TrackerProviderRegistration> registrations,
    ILogger<TrackerPollingCoordinator> logger) : BackgroundService
{
    private const string LeaseName = "tracker-polling";

    // The tracker types whose tasks are re-read by polling, not pushed by webhook.
    private readonly HashSet<string> _pollTypes = registrations
        .Where(r => r.Ingestion == IngestionMode.Poll)
        .Select(r => ProviderKey.Normalize(r.ProviderType))
        .ToHashSet(StringComparer.Ordinal);

    // Per-connection last-sweep wall-clock, so each connection's interval is honoured. In-memory and
    // per replica: on a lease hand-off the new holder sweeps each connection once immediately, which is
    // harmless because re-reading a task is idempotent.
    private readonly Dictionary<Guid, DateTime> _lastPolled = [];

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled)
        {
            logger.LogInformation("Tracker polling is disabled");
            return;
        }

        // No poll-mode tracker type registered: nothing sweeps, so do not even hold a timer or lease.
        if (_pollTypes.Count == 0) return;

        var interval = TimeSpan.FromSeconds(Math.Max(options.Value.IntervalSeconds, 5));
        using var timer = new PeriodicTimer(interval, clock);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await timer.WaitForNextTickAsync(stoppingToken)) return;

                await using var held = await lease.TryAcquireAsync(LeaseName, interval, stoppingToken);
                if (held is null) continue;

                await PollAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Tracker polling pass failed");
            }
        }
    }

    private async Task PollAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var bus = scope.ServiceProvider.GetRequiredService<IBus>();

        // Filtered in memory by normalized provider type, exactly as the VCS poller does.
        var connections = (await db.TrackerConnections.AsNoTracking().ToListAsync(ct))
            .Where(c => _pollTypes.Contains(ProviderKey.Normalize(c.ProviderType)))
            .ToList();

        var now = clock.GetUtcNow().UtcDateTime;

        // Drop connections that left poll mode so the map does not grow unbounded.
        var live = connections.Select(c => c.Id).ToHashSet();
        foreach (var gone in _lastPolled.Keys.Where(k => !live.Contains(k)).ToList())
            _lastPolled.Remove(gone);

        foreach (var connection in connections)
        {
            var configured = VcsPollSettings.IntervalFrom(ProviderSettingsBag.Deserialize(connection.ProviderSettingsJson));
            var due = TimeSpan.FromSeconds(Math.Max(configured, options.Value.IntervalSeconds));
            if (_lastPolled.TryGetValue(connection.Id, out var last) && now - last < due)
                continue;
            _lastPolled[connection.Id] = now;

            try
            {
                // Re-read the connection's open tasks: a closed task can no longer change a plan. The
                // consumer re-reads each from the tracker, refreshing status and dependency links.
                var externalIds = await db.Tasks
                    .Where(t => t.ClosedAt == null && t.TrackerConnectionId == connection.Id)
                    .OrderBy(t => t.Id)
                    .Take(options.Value.MaxTasksPerRun)
                    .Select(t => t.ExternalId)
                    .AsNoTracking()
                    .ToListAsync(ct);

                foreach (var externalId in externalIds)
                    await bus.Send(new TaskSyncRequested(connection.Name, externalId, "Tracker poll"));

                if (externalIds.Count > 0)
                    logger.LogDebug("Polled {Count} open task(s) from tracker {Connection}", externalIds.Count, connection.Name);
            }
            catch (Exception ex)
            {
                // One connection's failure must not stop the others.
                logger.LogError(ex, "Polling tracker connection {Connection} failed", connection.Name);
            }
        }
    }
}
