using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ReleaseOrchestrator.Application.DTOs;
using ReleaseOrchestrator.Application.Services;
using ReleaseOrchestrator.Core.Entities;
using ReleaseOrchestrator.Core.Parsing;
using ReleaseOrchestrator.Infrastructure.Auth;
using ReleaseOrchestrator.Infrastructure.Persistence;

namespace ReleaseOrchestrator.Infrastructure.Vcs;

/// <summary>
/// Reads a merge request from the VCS API into the local model.
///
/// The webhook path is the primary source; this exists to reconcile what webhooks missed
/// (delivery failures, downtime). Both paths must agree on how state becomes status — they
/// used to keep separate mappings, so the same MR ended up with a different status depending
/// on which path imported it.
/// </summary>
public class VcsService(
    AppDbContext db,
    IVcsApiClient apiClient,
    TokenProtector protector,
    TimeProvider clock,
    ILogger<VcsService> logger) : IVcsService
{
    public async Task SyncMergeRequestAsync(Guid repositoryId, string externalMrId, CancellationToken ct)
    {
        var repo = await db.Repositories
            .Include(r => r.Connection)
            .Include(r => r.TrackerConnection)
            .FirstOrDefaultAsync(r => r.Id == repositoryId, ct)
            ?? throw new InvalidOperationException($"Repository {repositoryId} not found");

        var token = protector.Unprotect(repo.Connection.EncryptedAccessToken);
        var info = await apiClient.GetMergeRequestAsync(repo.Connection.ApiUrl, token, repo.ExternalId, externalMrId, ct);
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
        mr.TaskExternalId = BranchTaskParser.ParseTaskId(info.SourceBranch);
        mr.TaskId = await ResolveTaskIdAsync(repo, mr.TaskExternalId, ct) ?? mr.TaskId;

        ApplyStatus(mr, info);

        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Mirrors the webhook consumers' rules exactly: a terminal state is final and clears a manual
    /// pin, an open MR's deployability comes from the connection's label unless an operator pinned
    /// it, and merged and closed carry distinct timestamps — archiving needs one of them set.
    /// </summary>
    private void ApplyStatus(MergeRequest mr, VcsApiMrInfo info)
    {
        var status = MergeRequestStatusResolver.FromVcsState(info.State);
        if (status is null)
        {
            logger.LogWarning("MR {Mr} has an unmodelled state '{State}'; status left unchanged.", mr.ExternalId, info.State);
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

        // The API does not return labels, so a label-driven promotion cannot be re-derived here.
        // Leaving a pinned or already-promoted status alone is the safe read: the webhook owns
        // promotion, and this path must not silently demote an MR out of the plan.
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
