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

/// <summary>Tunables for the VCS poller.</summary>
public class VcsPollingOptions
{
    /// <summary>Whether the poller runs. Off in tests.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>How often the poller wakes and sweeps the poll-mode connections, in seconds.</summary>
    public int IntervalSeconds { get; set; } = 60;
}

/// <summary>
/// Polls the merge requests of connections whose provider type is registered <see cref="IngestionMode.Poll"/>
/// and emits the same <see cref="MrOpened"/> events the webhook front door does -- the pull half of the
/// symmetric ingestion seam.
/// </summary>
/// <remarks>
/// Reuses the read provider (<see cref="IVcsProvider.GetOpenMergeRequestsAsync"/>): no provider-side
/// polling code is needed. Each observation carries a deterministic event id, so the dedup inbox
/// drops an unchanged merge request and only a change (status, labels) is processed. Leased, so one
/// replica polls at a time.
///
/// Each connection is swept on its own interval -- the <see cref="VcsPollSettings.IntervalKey"/>
/// setting the poll provider type declares -- with the global tick (<c>IntervalSeconds</c>) as the
/// floor, since the poller cannot wake more often than it ticks.
///
/// NOT VERIFIED against a live GitLab. Remaining limitation for this cut: it lists OPEN merge
/// requests, so a transition to merged/closed (the merge request leaving the open list) is not
/// emitted here -- that still comes via a webhook. A follow-up.
/// </remarks>
public class VcsPollingCoordinator(
    IServiceScopeFactory scopeFactory,
    IDistributedLease lease,
    TimeProvider clock,
    IOptions<VcsPollingOptions> options,
    IEnumerable<VcsProviderRegistration> registrations,
    IngestionActivity activity,
    ILogger<VcsPollingCoordinator> logger) : BackgroundService
{
    private const string LeaseName = "vcs-polling";

    // The provider types whose events arrive by polling, not push. This replaced the per-connection
    // IngestionMode column: push versus poll is now a property of the provider type, so a connection
    // is polled exactly when its type is registered Poll.
    private readonly HashSet<string> _pollTypes = registrations
        .Where(r => r.Ingestion == IngestionMode.Poll)
        .Select(r => ProviderKey.Normalize(r.ProviderType))
        .ToHashSet(StringComparer.Ordinal);

    // Per-connection last-sweep wall-clock, so each connection's interval is honoured. In-memory and
    // per replica: on a lease hand-off the new holder starts fresh and sweeps each connection once
    // immediately, which is harmless -- the deterministic event id dedups an unchanged merge request.
    private readonly Dictionary<Guid, DateTime> _lastPolled = [];

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(options.Value.IntervalSeconds, 5));

        // Declared before the first tick, so the operations screen shows a worker that is switched
        // off as switched off rather than as absent.
        activity.Declare(IngestionWorker.VcsPolling, options.Value.Enabled, (int)interval.TotalSeconds);

        if (!options.Value.Enabled)
        {
            logger.LogInformation("VCS polling is disabled");
            return;
        }

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
                    // Another replica is sweeping. Said out loud, because "idle" and "someone else is
                    // doing it" look identical from here and mean different things.
                    activity.NotLeader(IngestionWorker.VcsPolling);
                    continue;
                }

                using var run = activity.Begin(IngestionWorker.VcsPolling);
                try
                {
                    await PollAsync(run, stoppingToken);
                }
                catch (Exception ex)
                {
                    run.Failed(ex);
                    throw;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "VCS polling pass failed");
            }
        }
    }

    private async Task PollAsync(IngestionActivity.IngestionRun run, CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var poller = scope.ServiceProvider.GetRequiredService<VcsConnectionPoller>();

        // Filtered in memory by normalized provider type: the poll-type set is small and the provider
        // type is compared canonically, which a SQL string match would not do.
        var connections = (await db.VcsConnections
                .Include(c => c.Repositories)
                .AsNoTracking()
                .ToListAsync(ct))
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
            // Honour the per-connection cadence, with the global interval as the floor. The interval
            // is now a provider setting (the poll type declares it) rather than a column.
            var configured = VcsPollSettings.IntervalFrom(ProviderSettingsBag.Deserialize(connection.ProviderSettingsJson));
            var due = TimeSpan.FromSeconds(Math.Max(configured, options.Value.IntervalSeconds));
            if (_lastPolled.TryGetValue(connection.Id, out var last) && now - last < due)
            {
                continue;
            }

            _lastPolled[connection.Id] = now;

            try
            {
                // The same per-connection poll the manual "poll now" endpoint runs, so scheduled and
                // manual sweeps cannot diverge on how an open merge request becomes an event.
                var result = await poller.PollAsync(connection, ct);

                run.Emitted(result.Emitted);
                activity.Polled(
                    IngestionConnectionKind.Vcs, connection.Name, result.Emitted, result.Branches,
                    // The repositories it could not read, named: a connection that half worked is the
                    // case an operator most needs to see, and a green row would hide it.
                    result.Failures.Count == 0
                        ? null
                        : string.Join("; ", result.Failures.Select(f => $"{f.Repository}: {f.Reason}")));
            }
            catch (Exception ex)
            {
                // One connection's failure must not stop the others.
                logger.LogError(ex, "Polling connection {Connection} failed", connection.Name);
                activity.Polled(IngestionConnectionKind.Vcs, connection.Name, 0, 0, ex.Message);
            }
        }
    }
}
