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
    /// <returns>What was emitted, and which repositories could not be read.</returns>
    /// <remarks>
    /// A repository that cannot be read is recorded and skipped rather than thrown: one mistyped path
    /// (GitLab wants the full <c>group/project</c>, so a bare name is a 404) used to abort the whole
    /// connection's sweep and surface as a blanket 500, hiding both which repository was wrong and the
    /// results of every other repository.
    /// </remarks>
    public async Task<VcsPollResult> PollAsync(VcsConnection connection, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var provider = await factory.CreateAsync(connection.ToDescriptor(), ct);
        var source = $"{ProviderKey.Normalize(connection.ProviderType)}/{connection.Name}";
        var emitted = 0;
        var branchesSeen = 0;
        var failures = new List<VcsPollFailure>();

        foreach (var repository in connection.Repositories)
        {
            try
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

                // Branches too, not only merge requests: a branch with no merge request is work that
                // started and has not landed, which is what holds a parent task back. Skipped when the
                // provider cannot report branches, so "cannot say" never reads as "no work".
                if (provider.Capabilities.SupportsBranches)
                {
                    var branches = (await provider.GetBranchesAsync(repository.ExternalId, ct))
                        .Select(b => new BranchesObserved.Branch(b.Name, b.IsMerged, b.IsDefault))
                        .ToList();

                    await bus.Send(new BranchesObserved(
                        ConnectionName: connection.Name,
                        RepositoryExternalId: repository.ExternalId,
                        Branches: branches,
                        Source: source,
                        // Folds the whole set in, so an unchanged repository is dropped by the inbox and
                        // only a real change (a branch added, landed or deleted) costs any work.
                        EventId: PollingEventId.For(
                            source, repository.ExternalId, "branches", string.Empty,
                            branches.Select(b => $"{b.Name}:{(b.IsMerged ? 1 : 0)}").ToList(), null)));
                    branchesSeen += branches.Count;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failures.Add(new VcsPollFailure(repository.ExternalId, Describe(ex)));
                logger.LogWarning(
                    ex, "Polling repository {Repository} of {Connection} failed", repository.ExternalId, connection.Name);
            }
        }

        if (emitted > 0 || branchesSeen > 0)
            logger.LogDebug(
                "Polled {Count} open merge request(s) and {Branches} branch(es) from {Connection}",
                emitted, branchesSeen, connection.Name);
        return new VcsPollResult(emitted, failures, branchesSeen);
    }

    // A 404 from the provider is the common misconfiguration, and "Not Found" alone does not say what to
    // fix — name the likely cause instead of echoing the transport error.
    private static string Describe(Exception ex) =>
        ex is HttpRequestException { StatusCode: System.Net.HttpStatusCode.NotFound }
            ? "not found — check the repository's external id (GitLab wants the full path, e.g. group/project) and the token's access"
            : ex.Message;
}

/// <summary>What one connection's poll produced.</summary>
/// <param name="Emitted">How many merge-request observations were sent.</param>
/// <param name="Failures">Repositories that could not be read; empty when every one succeeded.</param>
/// <param name="Branches">How many branches were observed across the connection's repositories.</param>
public sealed record VcsPollResult(int Emitted, IReadOnlyList<VcsPollFailure> Failures, int Branches = 0);

/// <summary>A repository the poll could not read, and why.</summary>
/// <param name="RepositoryExternalId">The repository, as configured.</param>
/// <param name="Reason">A human explanation, aimed at the misconfiguration that usually causes it.</param>
public sealed record VcsPollFailure(string RepositoryExternalId, string Reason);
