using Microsoft.Extensions.Logging;
using Rebus.Bus;
using ReleaseOrchestrator.Application.Contracts.Messages;
using ReleaseOrchestrator.Infrastructure.Persistence.Models;
using ReleaseOrchestrator.Infrastructure.Providers;
using ReleaseOrchestrator.Providers.Abstractions;
using ReleaseOrchestrator.Providers.Abstractions.Vcs;

namespace ReleaseOrchestrator.Infrastructure.Ingestion;

/// <summary>
/// Polls one VCS connection's open merge requests now, emitting the same <see cref="MrOpened"/> events
/// the webhook front door does.
/// </summary>
/// <remarks>
/// Shared by the scheduled poller (<see cref="VcsPollingCoordinator"/>) and the manual "poll now"
/// endpoint, so the two cannot drift on how a polled merge request becomes an event — the deterministic
/// <see cref="PollingEventId"/> means a manual poll of an unchanged merge request is deduplicated exactly
/// like a scheduled one.
/// </remarks>
public sealed class VcsConnectionPoller(
    IVcsProviderFactory factory,
    IBus bus,
    ILogger<VcsConnectionPoller> logger)
{
    /// <summary>Polls every repository of the connection and emits one event per open merge request.</summary>
    /// <param name="connection">The connection, with its <see cref="VcsConnection.Repositories"/> loaded.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>How many merge-request observations were emitted.</returns>
    public async Task<int> PollAsync(VcsConnection connection, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(connection);

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
                    // The task key is resolved by the consumer from the connection's rule; the poll
                    // carries the raw candidates, like the webhook path.
                    Title: mr.Title,
                    Labels: mr.Labels,
                    // Read from the API here, unlike a merge-request webhook, so a polled connection can
                    // gate on pipeline:* too.
                    PipelineResult: mr.PipelineStatus,
                    Source: source,
                    EventId: PollingEventId.For(
                        source, repository.ExternalId, mr.Id, mr.Status?.ToString() ?? string.Empty,
                        mr.Labels, mr.PipelineStatus)));
                emitted++;
            }
        }

        if (emitted > 0)
            logger.LogDebug("Polled {Count} open merge request(s) from {Connection}", emitted, connection.Name);
        return emitted;
    }
}
