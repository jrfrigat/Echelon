using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ReleaseOrchestrator.Application.DTOs;
using ReleaseOrchestrator.Application.Services;
using ReleaseOrchestrator.Core.Entities;
using ReleaseOrchestrator.Core.Parsing;
using ReleaseOrchestrator.Infrastructure.Persistence;
using ReleaseOrchestrator.Providers.Abstractions.Vcs;

namespace ReleaseOrchestrator.Infrastructure.Vcs;

/// <summary>
/// Reads a merge request from the VCS API into the local model.
///
/// The webhook path is the primary source; this exists to reconcile what webhooks missed
/// (delivery failures, downtime). Both paths must agree on how state becomes status — they
/// used to keep separate mappings, so the same MR ended up with a different status depending
/// on which path imported it.
/// </summary>
/// <remarks>
/// Provider-agnostic: it asks the factory for whatever adapter the connection names and works in
/// normalized terms. It used to take an <c>IVcsApiClient</c> that only GitLab implemented, and to
/// pass that client the connection's URL and token on every call.
/// </remarks>
public class VcsService(
    AppDbContext db,
    IVcsProviderFactory providerFactory,
    TimeProvider clock,
    ILogger<VcsService> logger) : IVcsService
{
    /// <inheritdoc/>
    public async Task SyncMergeRequestAsync(Guid repositoryId, string externalMrId, CancellationToken ct)
    {
        var repo = await db.Repositories
            .Include(r => r.Connection)
            .Include(r => r.TrackerConnection)
            .FirstOrDefaultAsync(r => r.Id == repositoryId, ct)
            ?? throw new InvalidOperationException($"Repository {repositoryId} not found");

        var provider = await providerFactory.CreateAsync(repo.Connection, ct);
        var info = await provider.GetMergeRequestAsync(repo.ExternalId, externalMrId, ct);
        if (info is null)
        {
            logger.LogInformation("MR {Mr} no longer exists in {Repo}", externalMrId, repo.Name);
            return;
        }

        var mr = await db.MergeRequests
            .FirstOrDefaultAsync(m => m.RepositoryId == repositoryId && m.ExternalId == externalMrId, ct);

        if (mr is null)
        {
            mr = new MergeRequest
            {
                Id = Guid.NewGuid(),
                ExternalId = externalMrId,
                RepositoryId = repositoryId,
                CreatedAt = info.CreatedAt
            };
            db.MergeRequests.Add(mr);
        }

        mr.SourceBranch = info.SourceBranch;
        mr.TargetBranch = info.TargetBranch;
        // The provider owns the branch-key dialect: the key format belongs to whoever spells it,
        // not to this service.
        mr.TaskExternalId = provider.ParseTaskKeyFromBranch(info.SourceBranch);
        mr.TaskId = await ResolveTaskIdAsync(repo, mr.TaskExternalId, ct) ?? mr.TaskId;

        ApplyStatus(mr, info);

        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Mirrors the webhook consumers' rules exactly: a terminal state is final and clears a manual
    /// pin, an open MR's deployability comes from the connection's label unless an operator pinned
    /// it, and merged and closed carry distinct timestamps — archiving needs one of them set.
    /// </summary>
    private void ApplyStatus(MergeRequest mr, VcsMergeRequest info)
    {
        // Already normalized by the adapter; a null means the provider reported a state its
        // adapter does not model, which is not something to guess at.
        var status = info.Status;
        if (status is null)
        {
            logger.LogWarning("MR {Mr} has an unmodelled state; status left unchanged.", mr.ExternalId);
            return;
        }

        if (MergeRequestStatusResolver.IsTerminal(mr.Status) && !MergeRequestStatusResolver.IsTerminal(status.Value))
            return;

        if (MergeRequestStatusResolver.IsTerminal(status.Value))
        {
            mr.Status = status.Value;
            mr.IsStatusManual = false;
            if (status.Value == Core.Enums.MergeRequestStatus.Merged)
                mr.MergedAt = info.MergedAt ?? clock.GetUtcNow().UtcDateTime;
            else
                mr.ClosedAt = clock.GetUtcNow().UtcDateTime;
            return;
        }

        // Reopened: shed the terminal timestamps or archiving still claims it.
        mr.MergedAt = null;
        mr.ClosedAt = null;

        // Promotion is still the webhook's: this path never demotes an MR out of the plan.
        //
        // The old reason for that — "the API does not return labels" — no longer holds, since the
        // provider reports them and Capabilities.SupportsMergeRequestLabels says whether to
        // believe an empty list. Re-deriving the status here would be a change to when an MR
        // leaves a release plan, which is not a side effect to smuggle into a refactor; it is
        // left as a deliberate follow-up.
        if (!mr.IsStatusManual && mr.Status != Core.Enums.MergeRequestStatus.ReadyForDeploy)
            mr.Status = status.Value;
    }

    private async Task<Guid?> ResolveTaskIdAsync(Repository repo, string? taskExternalId, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(taskExternalId)) return null;

        var query = db.Tasks.Where(t => t.ExternalId == taskExternalId);
        if (repo.TrackerConnectionId is { } trackerId)
            query = query.Where(t => t.TrackerConnectionId == trackerId);

        var candidates = await query.Select(t => (Guid?)t.Id).Take(2).ToListAsync(ct);
        return candidates.Count == 1 ? candidates[0] : null;
    }

    public async Task<MergeRequestDto?> GetMergeRequestAsync(string connectionName, string projectPath, string iid, CancellationToken ct)
    {
        var conn = await db.VcsConnections.FirstOrDefaultAsync(c => c.Name == connectionName, ct);
        if (conn is null) return null;

        var repo = await db.Repositories.FirstOrDefaultAsync(r => r.ConnectionId == conn.Id && r.ExternalId == projectPath, ct);
        if (repo is null) return null;

        var mr = await db.MergeRequests.FirstOrDefaultAsync(m => m.RepositoryId == repo.Id && m.ExternalId == iid, ct);

        return mr is null
            ? null
            : new MergeRequestDto(mr.Id, mr.ExternalId, mr.SourceBranch, mr.TargetBranch, mr.RepositoryId, mr.TaskId, mr.Status, mr.CreatedAt, mr.MergedAt);
    }
}
