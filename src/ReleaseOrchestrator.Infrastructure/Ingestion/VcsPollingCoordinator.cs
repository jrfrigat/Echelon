using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Rebus.Bus;
using ReleaseOrchestrator.Application.Contracts.Messages;
using ReleaseOrchestrator.Application.Services;
using ReleaseOrchestrator.Core.Enums;
using ReleaseOrchestrator.Infrastructure.Persistence;
using ReleaseOrchestrator.Infrastructure.Providers;
using ReleaseOrchestrator.Providers.Abstractions;
using ReleaseOrchestrator.Providers.Abstractions.Vcs;

namespace ReleaseOrchestrator.Infrastructure.Ingestion;

/// <summary>Tunables for the VCS poller.</summary>
public class VcsPollingOptions
{
    /// <summary>Whether the poller runs. Off in tests.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>How often the poller wakes and sweeps the poll-mode connections, in seconds.</summary>
    public int IntervalSeconds { get; set; } = 60;
}

/// <summary>
/// Polls the merge requests of connections set to <see cref="IngestionMode.Poll"/> and emits the same
/// <see cref="MrOpened"/> events the webhook front door does -- the pull half of the symmetric
/// ingestion seam (docs/issues/008-ingestion-and-messaging.md).
/// </summary>
/// <remarks>
/// Reuses the read provider (<see cref="IVcsProvider.GetOpenMergeRequestsAsync"/>): no provider-side
/// polling code is needed. Each observation carries a deterministic event id, so the dedup inbox
/// drops an unchanged merge request and only a change (status, labels) is processed. Leased, so one
/// replica polls at a time.
///
/// Each connection is swept on its own <c>PollIntervalSeconds</c>, with the global tick
/// (<c>IntervalSeconds</c>) as the floor -- the poller cannot wake more often than it ticks.
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
    ILogger<VcsPollingCoordinator> logger) : BackgroundService
{
    private const string LeaseName = "vcs-polling";

    // Per-connection last-sweep wall-clock, so PollIntervalSeconds is honoured. In-memory and per
    // replica: on a lease hand-off the new holder starts fresh and sweeps each connection once
    // immediately, which is harmless -- the deterministic event id dedups an unchanged merge request.
    private readonly Dictionary<Guid, DateTime> _lastPolled = [];

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled)
        {
            logger.LogInformation("VCS polling is disabled");
            return;
        }

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
                logger.LogError(ex, "VCS polling pass failed");
            }
        }
    }

    private async Task PollAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var factory = scope.ServiceProvider.GetRequiredService<IVcsProviderFactory>();
        var bus = scope.ServiceProvider.GetRequiredService<IBus>();

        var connections = await db.VcsConnections
            .Where(c => c.IngestionMode == IngestionMode.Poll)
            .Include(c => c.Repositories)
            .AsNoTracking()
            .ToListAsync(ct);

        var now = clock.GetUtcNow().UtcDateTime;

        // Drop connections that left poll mode so the map does not grow unbounded.
        var live = connections.Select(c => c.Id).ToHashSet();
        foreach (var gone in _lastPolled.Keys.Where(k => !live.Contains(k)).ToList())
            _lastPolled.Remove(gone);

        foreach (var connection in connections)
        {
            // Honour the per-connection cadence, with the global interval as the floor.
            var due = TimeSpan.FromSeconds(Math.Max(connection.PollIntervalSeconds, options.Value.IntervalSeconds));
            if (_lastPolled.TryGetValue(connection.Id, out var last) && now - last < due)
                continue;
            _lastPolled[connection.Id] = now;

            try
            {
                var provider = await factory.CreateAsync(connection.ToDescriptor(), ct);
                var source = $"{ProviderKey.Normalize(connection.ProviderType)}/{connection.Name}";
                var emitted = 0;

                foreach (var repository in connection.Repositories)
                {
                    var mrs = await provider.GetOpenMergeRequestsAsync(repository.ExternalId, ct);
                    foreach (var mr in mrs)
                    {
                        await bus.Send(new MrOpened(
                            ConnectionName: connection.Name,
                            RepositoryExternalId: repository.ExternalId,
                            ExternalMrId: mr.Id,
                            SourceBranch: mr.SourceBranch,
                            TargetBranch: mr.TargetBranch,
                            TaskExternalId: provider.ParseTaskKeyFromBranch(mr.SourceBranch),
                            Labels: mr.Labels,
                            Source: source,
                            EventId: PollingEventId.For(source, repository.ExternalId, mr.Id, mr.Status?.ToString() ?? string.Empty, mr.Labels)));
                        emitted++;
                    }
                }

                if (emitted > 0)
                    logger.LogDebug("Polled {Count} open merge request(s) from {Connection}", emitted, connection.Name);
            }
            catch (Exception ex)
            {
                // One connection's failure must not stop the others.
                logger.LogError(ex, "Polling connection {Connection} failed", connection.Name);
            }
        }
    }
}
