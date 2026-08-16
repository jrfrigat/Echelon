using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Rebus.Handlers;
using Echelon.Application.Contracts.Messages;
using Echelon.Core.Parsing;
using Echelon.Infrastructure.Persistence;
using Echelon.Infrastructure.Persistence.Models;
using Echelon.Providers.Abstractions;
using Echelon.Providers.Abstractions.Vcs;

namespace Echelon.Infrastructure.Queue.Consumers;

/// <summary>
/// Reconciles a repository's branches: upserts the ones observed, drops the ones that are gone, and
/// links each to the task it names by the connection's own linking rule.
/// </summary>
/// <remarks>
/// This is what makes a branch visible to planning. A branch with no merge request is work that has
/// started and not landed, so the task that owns it is not finished - and the launch guard reads these
/// rows to hold back a parent whose child is still in progress. The link uses the same
/// <see cref="TaskKeyExtractor"/> and the same per-connection rule the merge-request path uses, so a
/// branch and its eventual merge request can never be attributed to different tasks.
/// </remarks>
public class BranchesObservedConsumer(
    AppDbContext db,
    TimeProvider clock,
    ILogger<BranchesObservedConsumer> logger) : IHandleMessages<BranchesObserved>
{
    /// <inheritdoc/>
    public async Task Handle(BranchesObserved message)
    {
        var msg = message;
        var ct = HandlerCancellation.Token;

        var repo = await db.Repositories
            .Include(r => r.Connection)
            .FirstOrDefaultAsync(
                r => r.Connection.Name == msg.ConnectionName && r.ExternalId == msg.RepositoryExternalId, ct);

        if (repo is null)
        {
            // Not retryable: the repository is absent from configuration and redelivery will not
            // conjure it. Same call as the merge-request path makes.
            logger.LogWarning(
                "Repository not found for connection={Connection}, path={Path}; ignoring branch snapshot",
                msg.ConnectionName, msg.RepositoryExternalId);
            return;
        }

        var (source, pattern) = TaskLinkSettings.RuleFrom(
            ProviderSettingsBag.Deserialize(repo.Connection.ProviderSettingsJson));

        var now = clock.GetUtcNow().UtcDateTime;
        var existing = await db.RepositoryBranches
            .Where(b => b.RepositoryId == repo.Id)
            .ToListAsync(ct);
        var byName = existing.ToDictionary(b => b.Name, StringComparer.Ordinal);

        // Deduplicated before the loop, not trusted from the wire. (RepositoryId, Name) is unique, so a
        // snapshot naming the same branch twice would queue two inserts, and SaveChanges would fail the
        // whole handler -- which under at-least-once delivery is a message that redelivers and fails
        // forever, taking every later branch update for the repository with it. Last entry wins, matching
        // the upsert below.
        var observedBranches = msg.Branches
            .Where(b => !string.IsNullOrWhiteSpace(b.Name))
            .GroupBy(b => b.Name, StringComparer.Ordinal)
            .Select(g => g.Last())
            .ToList();

        foreach (var observed in observedBranches)
        {
            // A branch names its task through the connection's rule; the title and labels a merge
            // request would offer do not exist here, so only the branch name is a candidate.
            var taskExternalId = TaskKeyExtractor.Extract(
                source, pattern, observed.Name, title: null, labels: null);

            if (byName.TryGetValue(observed.Name, out var row))
            {
                row.IsMerged = observed.IsMerged;
                row.IsDefault = observed.IsDefault;
                row.TaskExternalId = taskExternalId;
                row.LastSeenAt = now;
            }
            else
            {
                db.RepositoryBranches.Add(new RepositoryBranch
                {
                    Id = Guid.NewGuid(),
                    RepositoryId = repo.Id,
                    Name = observed.Name,
                    TaskExternalId = taskExternalId,
                    IsMerged = observed.IsMerged,
                    IsDefault = observed.IsDefault,
                    FirstSeenAt = now,
                    LastSeenAt = now
                });
            }
        }

        // Gone from the snapshot means gone from the repository (merged and deleted, or abandoned), and
        // a branch that no longer exists must stop blocking anything - the one direction this must
        // never get wrong.
        var observedNames = observedBranches.Select(b => b.Name).ToHashSet(StringComparer.Ordinal);
        var vanished = existing.Where(b => !observedNames.Contains(b.Name)).ToList();
        if (vanished.Count > 0) db.RepositoryBranches.RemoveRange(vanished);

        await db.SaveChangesAsync(ct);

        logger.LogDebug(
            "Reconciled {Observed} branch(es) for {Repository}; removed {Removed}",
            observedBranches.Count, repo.ExternalId, vanished.Count);
    }
}
