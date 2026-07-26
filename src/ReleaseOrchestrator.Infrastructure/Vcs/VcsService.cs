using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ReleaseOrchestrator.Application.DTOs;
using ReleaseOrchestrator.Application.Services;
using ReleaseOrchestrator.Infrastructure.Persistence.Models;
using ReleaseOrchestrator.Core.Parsing;
using ReleaseOrchestrator.Infrastructure.Audit;
using ReleaseOrchestrator.Infrastructure.Providers;
using ReleaseOrchestrator.Infrastructure.Persistence;
using ReleaseOrchestrator.Providers.Abstractions;
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

        var provider = await providerFactory.CreateAsync(repo.Connection.ToDescriptor(), ct);
        var info = await provider.GetMergeRequestAsync(repo.ExternalId, externalMrId, ct);
        if (info is null)
        {
            logger.LogInformation("MR {Mr} no longer exists in {Repo}", externalMrId, repo.Name);
            return;
        }

        var mr = await db.MergeRequests
            .FirstOrDefaultAsync(m => m.RepositoryId == repositoryId && m.ExternalId == externalMrId, ct);

        // Whether this poll is the first time we have seen the merge request decides whether its
        // status journal entry has a "from" or opens the history.
        var isNew = mr is null;

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
        // The task key comes from the connection's rule -- which field, which pattern -- applied to the
        // same candidates the ingestion paths use, so reconcile and the webhook agree on the link.
        var (source, pattern) = TaskLinkSettings.RuleFrom(
            ProviderSettingsBag.Deserialize(repo.Connection.ProviderSettingsJson));
        mr.TaskExternalId = TaskKeyExtractor.Extract(source, pattern, info.SourceBranch, info.Title, info.Labels);
        mr.TaskId = await ResolveTaskIdAsync(repo, mr.TaskExternalId, ct) ?? mr.TaskId;

        // Captured around the call rather than inside it: ApplyStatus assigns the status in three
        // branches and returns early from two more, so recording at each site would be five calls
        // that have to stay in step. Comparing before and after is one, and a new branch added later
        // is covered without anyone remembering to instrument it.
        var previousStatus = isNew ? (Core.Enums.MergeRequestStatus?)null : mr.Status;

        ApplyStatus(mr, info);

        MergeRequestStatusJournal.Record(
            db, mr, previousStatus, mr.Status,
            MergeRequestStatusJournal.CausePoll, ActorRef.System, clock.GetUtcNow().UtcDateTime);

        // Persist the full label set the per-environment readiness gate reads, exactly as the webhook
        // consumers do -- and the whole reason this reconcile exists. The dangerous case is a missed
        // "label removed" delivery: without refreshing the set here, the gate would keep admitting a
        // merge request to an environment on a ready-for-<env> label the provider no longer reports.
        // Only when the provider can actually report labels: when it cannot, an empty list means
        // "cannot say", not "none", and clearing the set on that would wipe what the webhook captured.
        if (provider.Capabilities.SupportsMergeRequestLabels)
            MergeRequestLabelJournal.Apply(
                db, mr, info.Labels, MergeRequestStatusJournal.CausePoll, ActorRef.System,
                clock.GetUtcNow().UtcDateTime);

        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Mirrors the webhook consumers' rules exactly: a terminal state is final and clears a manual
    /// pin, an open MR is Opened unless an operator pinned its status, and merged and closed carry
    /// distinct timestamps — archiving needs one of them set.
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

        // An open merge request is Opened unless an operator pinned its status. Deploy readiness is a
        // per-environment rule over signals evaluated at launch, not a status a label promotes it to,
        // so the reconcile no longer re-derives a status from labels -- the label SET the gate reads is
        // refreshed separately by the caller.
        mr.Status = MergeRequestStatusResolver.ResolveOpenStatus(mr.IsStatusManual, mr.Status);
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
