using Echelon.Application.Contracts.Messages;
using Echelon.Application.DTOs;
using Echelon.Infrastructure.Persistence;
using Echelon.Infrastructure.Persistence.Models;
using Echelon.Infrastructure.Providers;
using Echelon.Providers.Abstractions;
using Echelon.Providers.Abstractions.Tracker;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Rebus.Bus;

namespace Echelon.Infrastructure.Ingestion;

/// <summary>
/// Polls one tracker connection now: asks the tracker which issues are open, adds the tasks already
/// known to be open locally, and requests a sync for each - the same <see cref="TaskSyncRequested"/>
/// the webhook path raises.
/// </summary>
/// <remarks>
/// <para>
/// Shared by the scheduled sweep (<see cref="TrackerPollingCoordinator"/>) and the manual "poll now"
/// endpoint, so the two cannot drift, exactly as <see cref="VcsConnectionPoller"/> is shared on the VCS
/// side.
/// </para>
/// <para>
/// The two sources are a union, and both halves are load-bearing. Asking the tracker is what makes a
/// poll-mode connection able to start at all: polling used to read the open tasks out of the local
/// database, which never bootstraps, because a fresh install has no tasks - the sweep ran over an empty
/// set forever and a tracker that cannot push webhooks contributed nothing. Re-reading what is already
/// known is what notices the opposite case: an issue that was closed is no longer in the tracker's open
/// set, so a discovery-only sweep would never hear about it and the local copy would stay open for good.
/// </para>
/// <para>
/// A provider that cannot be searched - one that does not implement <see cref="ITrackerIssueSource"/> -
/// degrades to the known-tasks half rather than failing, which is what a webhook-mode connection wants
/// anyway: its tasks arrive by webhook, and this only refreshes them.
/// </para>
/// </remarks>
public sealed class TrackerConnectionPoller(
    ITrackerProviderFactory factory,
    AppDbContext db,
    IBus bus,
    IOptions<TrackerPollingOptions> options,
    ILogger<TrackerConnectionPoller> logger)
{
    /// <summary>Requests a sync for every open issue of the connection, discovered or already known.</summary>
    /// <param name="connection">The connection to poll.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>How many syncs were requested, how many of those the tracker turned up that were not known, and what went wrong if anything did.</returns>
    /// <remarks>
    /// A tracker that cannot be read is recorded and reported, not thrown: the known-open tasks are
    /// still worth re-reading, and the operator needs the tracker's own sentence - usually a missing
    /// queue setting or a rejected token - rather than a blanket 500 that hides both.
    /// </remarks>
    public async Task<TrackerPollResultDto> PollAsync(TrackerConnection connection, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var limit = Math.Max(options.Value.MaxTasksPerRun, 1);

        // Ordinal-ignore-case so a tracker that answers in a different case than the local copy is
        // stored in cannot queue the same task twice.
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // A closed task can no longer change a plan, so only the open ones are worth re-reading.
        var known = await db.Tasks
            .Where(t => t.ClosedAt == null && t.TrackerConnectionId == connection.Id)
            .OrderBy(t => t.Id)
            .Take(limit)
            .Select(t => t.ExternalId)
            .AsNoTracking()
            .ToListAsync(ct);

        foreach (var key in known)
        {
            keys.Add(key);
        }

        var (discovered, failure) = await DiscoverAsync(connection, keys, limit, ct);

        var source = $"{ProviderKey.Normalize(connection.ProviderType)}/{connection.Name}";
        foreach (var key in keys)
        {
            await bus.Send(new TaskSyncRequested(connection.Name, key, $"Tracker poll ({source})"));
        }

        if (keys.Count > 0)
        {
            logger.LogDebug(
                "Polled {Count} open task(s) from tracker {Connection}, {Discovered} of them new",
                keys.Count, connection.Name, discovered);
        }

        return new TrackerPollResultDto(keys.Count, discovered, failure);
    }

    /// <summary>Adds the tracker's own open issues to <paramref name="keys"/>.</summary>
    /// <returns>How many keys were new, and the failure that stopped the search if one did.</returns>
    private async Task<(int Discovered, string? Failure)> DiscoverAsync(
        TrackerConnection connection, HashSet<string> keys, int limit, CancellationToken ct)
    {
        try
        {
            // Connecting is part of the try: a connection missing a required setting throws here, and
            // that is exactly the misconfiguration the operator needs named.
            var provider = await factory.CreateAsync(connection.ToDescriptor(), ct);

            if (provider is not ITrackerIssueSource source)
            {
                logger.LogDebug(
                    "Tracker {Connection} cannot be searched; re-reading the {Count} task(s) already known",
                    connection.Name, keys.Count);
                return (0, null);
            }

            var open = await source.ListOpenIssueKeysAsync(limit, ct);
            var discovered = 0;

            foreach (var key in open.Where(k => !string.IsNullOrWhiteSpace(k)))
            {
                // The cap covers the union, not each half: a tracker with a large backlog must not be
                // able to queue more work than one sweep is allowed to.
                if (keys.Count >= limit)
                {
                    break;
                }

                if (keys.Add(key))
                {
                    discovered++;
                }
            }

            return (discovered, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Reading open issues from tracker {Connection} failed", connection.Name);
            return (0, ex.Message);
        }
    }
}


