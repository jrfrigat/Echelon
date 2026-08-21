using Echelon.Application.Contracts.Messages;
using Echelon.Application.Services;
using Echelon.Core.Enums;
using Echelon.Infrastructure.Persistence;
using Echelon.Providers.Abstractions;
using Echelon.Providers.Abstractions.Vcs;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

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
/// Sweeps the connections whose tracker provider type is registered <see cref="IngestionMode.Poll"/> on
/// each connection's own interval - the pull half of the tracker ingestion, symmetric with
/// <see cref="VcsPollingCoordinator"/>. Each sweep asks the tracker what is open and re-reads what is
/// already known, through <see cref="TrackerConnectionPoller"/>.
/// </summary>
/// <remarks>
/// <para>
/// Additive to <c>TaskReconciliationService</c>, which keeps every connection's dependency links fresh
/// on its own (coarser) global timer regardless of type. This gives a poll-mode connection - one the
/// tracker cannot push webhooks to - a faster, per-connection cadence, and the only way it ever learns
/// of a task at all: no webhook arrives, so nothing else would create one.
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
        if (_pollTypes.Count == 0)
        {
            return;
        }

        var interval = TimeSpan.FromSeconds(Math.Max(options.Value.IntervalSeconds, 5));
        using var timer = new PeriodicTimer(interval, clock);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await timer.WaitForNextTickAsync(stoppingToken))
                {
                    return;
                }

                await using var held = await lease.TryAcquireAsync(LeaseName, interval, stoppingToken);
                if (held is null)
                {
                    continue;
                }

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
        var poller = scope.ServiceProvider.GetRequiredService<TrackerConnectionPoller>();

        // Filtered in memory by normalized provider type, exactly as the VCS poller does.
        var connections = (await db.TrackerConnections.AsNoTracking().ToListAsync(ct))
            .Where(c => _pollTypes.Contains(ProviderKey.Normalize(c.ProviderType)))
            .ToList();

        var now = clock.GetUtcNow().UtcDateTime;

        // Drop connections that left poll mode so the map does not grow unbounded.
        var live = connections.Select(c => c.Id).ToHashSet();
        foreach (var gone in _lastPolled.Keys.Where(k => !live.Contains(k)).ToList())
        {
            _lastPolled.Remove(gone);
        }

        foreach (var connection in connections)
        {
            var configured = VcsPollSettings.IntervalFrom(ProviderSettingsBag.Deserialize(connection.ProviderSettingsJson));
            var due = TimeSpan.FromSeconds(Math.Max(configured, options.Value.IntervalSeconds));
            if (_lastPolled.TryGetValue(connection.Id, out var last) && now - last < due)
            {
                continue;
            }

            _lastPolled[connection.Id] = now;

            try
            {
                // The poller asks the tracker which issues are open and re-reads what is already
                // known locally; the consumer behind TaskSyncRequested refreshes each task's status
                // and dependency links, and creates the ones this connection had never seen.
                var result = await poller.PollAsync(connection, ct);

                // A tracker the sweep could not read is reported here and nowhere else: the manual
                // poll endpoint hands the reason back to the operator, but a scheduled sweep has only
                // the log, and "discovered nothing" and "could not ask" must not look the same.
                if (result.Failure is { Length: > 0 } failure)
                {
                    logger.LogWarning(
                        "Tracker connection {Connection} could not be searched: {Reason}",
                        connection.Name, failure);
                }
            }
            catch (Exception ex)
            {
                // One connection's failure must not stop the others.
                logger.LogError(ex, "Polling tracker connection {Connection} failed", connection.Name);
            }
        }
    }
}
