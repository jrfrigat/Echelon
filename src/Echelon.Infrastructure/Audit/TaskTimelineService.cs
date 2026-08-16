using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Echelon.Application.Auditing;
using Echelon.Application.DTOs;
using Echelon.Application.Services;
using Echelon.Core.Enums;
using Echelon.Infrastructure.Archive;
using Echelon.Infrastructure.Persistence;

namespace Echelon.Infrastructure.Audit;

/// <summary>
/// Builds a task's history by projecting the rows the system already writes.
/// </summary>
/// <remarks>
/// <para>
/// Each source is queried separately and the results merged in memory. Not a single UNION query on
/// purpose: the shapes differ, and the three providers in play - SQL Server, PostgreSQL and the
/// SQLite the tests run on - each render a heterogeneous UNION differently, which would make the
/// tests prove something other than what production does.
/// </para>
/// <para>
/// Every source is capped, and hitting a cap is reported rather than hidden. A task with a thousand
/// deploy attempts returns the most recent page and says it is truncated; silently dropping the
/// remainder would make an incomplete history look complete.
/// </para>
/// </remarks>
public class TaskTimelineService(AppDbContext db, ArchiveDbContext archiveDb) : ITaskTimelineService
{
    /// <inheritdoc/>
    public async Task<TaskTimelineDto?> GetAsync(Guid taskId, int limit = 200, CancellationToken ct = default)
    {
        var task = await db.Tasks
            .Where(t => t.Id == taskId)
            .Select(t => new { t.Id, t.ExternalId, t.ClosedAt, t.FirstSeenAt, t.FirstSeenSource })
            .AsNoTracking()
            .FirstOrDefaultAsync(ct);

        if (task is null)
        {
            // Archived, not absent. Answering 404 here would be indistinguishable from a mistyped id
            // and would tell an operator their task's history never existed, when in fact it was
            // deliberately preserved.
            var archived = await archiveDb.ArchivedTasks
                .Where(a => a.Id == taskId)
                .Select(a => new { a.Id, a.ExternalId, a.ClosedAt, a.FirstSeenAt, a.FirstSeenSource, a.ArchivedAt })
                .AsNoTracking()
                .FirstOrDefaultAsync(ct);

            if (archived is null) return null;

            return new TaskTimelineDto(
                archived.Id, archived.ExternalId, IsArchived: true,
                archived.FirstSeenAt, archived.FirstSeenSource,
                new TimelineCoverageDto(await RecordingBeganAtAsync(ct), Truncated: false, AttributionIsShared: false),
                await ArchivedEntriesAsync(archived.Id, archived.ExternalId, archived.ClosedAt, archived.ArchivedAt, ct));
        }

        var truncated = false;
        var entries = new List<TimelineEntryDto>();

        // ---- the task itself ------------------------------------------------

        if (task.FirstSeenAt is { } firstSeen)
            entries.Add(Entry(firstSeen, TimelineCategories.Task, TimelineKinds.TaskFirstSeen,
                subject: task.ExternalId, detail: task.FirstSeenSource));

        if (task.ClosedAt is { } closedAt)
            entries.Add(Entry(closedAt, TimelineCategories.Task, TimelineKinds.TaskClosed,
                subject: task.ExternalId, actorKind: ActorKinds.Webhook, clock: TimelineClockSources.External));

        // ---- merge requests -------------------------------------------------

        // The set is the UNION of what belongs to the task now and what belonged to it when the plan
        // and the rollouts were built. MergeRequest.TaskId is rewritten on every webhook delivery, so
        // selecting only by the current value would tear a task's history in half the moment a branch
        // is re-keyed -- and would graft those entries onto whichever task the merge request moved to.
        var currentMrIds = await db.MergeRequests
            .Where(m => m.TaskId == taskId).Select(m => m.Id).ToListAsync(ct);
        var planMrIds = await db.PlanItems
            .Where(i => i.PlanTaskNode.TaskId == taskId).Select(i => i.MergeRequestId).Distinct().ToListAsync(ct);
        var stepMrIds = await db.RolloutSteps
            .Where(s => s.TaskId == taskId).Select(s => s.MergeRequestId).Distinct().ToListAsync(ct);

        var everMrIds = currentMrIds.Union(planMrIds).Union(stepMrIds).ToList();
        var currentSet = currentMrIds.ToHashSet();

        var mergeRequests = await db.MergeRequests
            .Where(m => everMrIds.Contains(m.Id))
            .Select(m => new { m.Id, m.ExternalId, RepoName = m.Repository.Name, m.CreatedAt, m.MergedAt, m.ClosedAt })
            .AsNoTracking()
            .ToListAsync(ct);

        foreach (var mr in mergeRequests)
        {
            var subject = $"{mr.RepoName} !{mr.ExternalId}";
            var reassigned = !currentSet.Contains(mr.Id);

            // External clock: this column means "when the VCS opened it" on the poll path and "when
            // we received it" on the webhook path, and nothing in the row says which.
            entries.Add(Entry(mr.CreatedAt, TimelineCategories.MergeRequest, TimelineKinds.MrOpened,
                subject: subject, mergeRequestId: mr.Id, reassigned: reassigned,
                clock: TimelineClockSources.External));

            if (mr.MergedAt is { } mergedAt)
                entries.Add(Entry(mergedAt, TimelineCategories.MergeRequest, TimelineKinds.MrMerged,
                    subject: subject, mergeRequestId: mr.Id, reassigned: reassigned,
                    clock: TimelineClockSources.External));

            if (mr.ClosedAt is { } mrClosedAt)
                entries.Add(Entry(mrClosedAt, TimelineCategories.MergeRequest, TimelineKinds.MrClosed,
                    subject: subject, mergeRequestId: mr.Id, reassigned: reassigned,
                    clock: TimelineClockSources.External));
        }

        // Status transitions: the one part of this history that is recorded rather than derived,
        // because the status column is overwritten in place and keeps no previous value.
        var transitions = await db.MergeRequestStatusChanges
            .Where(c => c.TaskId == taskId || everMrIds.Contains(c.MergeRequestId))
            .OrderByDescending(c => c.At)
            .Take(limit)
            .AsNoTracking()
            .ToListAsync(ct);
        truncated |= transitions.Count == limit;

        // Qualified with the repository, like every other merge-request entry. A bare "118" is
        // ambiguous the moment two repositories both have one, and it also made two such entries
        // compare equal in the ordering's tie-break, which is what the total order exists to avoid.
        // The denormalised id remains the fallback: it is all that survives a hard-deleted MR.
        var subjectByMr = mergeRequests.ToDictionary(m => m.Id, m => $"{m.RepoName} !{m.ExternalId}");

        foreach (var change in transitions)
            entries.Add(Entry(change.At, TimelineCategories.MergeRequest, TimelineKinds.MrStatusChanged,
                subject: subjectByMr.GetValueOrDefault(change.MergeRequestId) ?? change.MergeRequestExternalId,
                detail: $"{change.FromStatus ?? "-"} -> {change.ToStatus} ({change.Cause})",
                actorOid: change.ActorOid, actorKind: change.ActorKind, actorName: change.ActorName,
                mergeRequestId: change.MergeRequestId,
                reassigned: !currentSet.Contains(change.MergeRequestId) && currentSet.Count > 0));

        // ---- plan versions --------------------------------------------------

        var plans = await db.RolloutPlans
            .Where(p => p.TargetTaskId == taskId)
            .OrderByDescending(p => p.CreatedAt)
            .Take(limit)
            .Select(p => new { p.Id, p.Version, p.CreatedAt, p.ContentHash, p.CreatedByOid, p.CreatedByKind, p.CreatedByName })
            .AsNoTracking()
            .ToListAsync(ct);
        truncated |= plans.Count == limit;

        entries.AddRange(CollapsePlanVersions(plans
            .Select(p => new PlanVersion(p.CreatedAt, p.Version, p.ContentHash, p.CreatedByOid, p.CreatedByKind, p.CreatedByName))
            .ToList()));

        // ---- rollouts -------------------------------------------------------

        var rollouts = await db.Rollouts
            .Where(r => r.TargetTaskId == taskId)
            .Select(r => new
            {
                r.Id, r.StartedAt, r.FinishedAt, r.Status,
                EnvKey = r.Environment.Key,
                r.LaunchedByOid, r.LaunchedByKind, r.LaunchedByName,
                StepCount = r.Steps.Count
            })
            .AsNoTracking()
            .ToListAsync(ct);

        foreach (var rollout in rollouts)
        {
            entries.Add(Entry(rollout.StartedAt, TimelineCategories.Rollout, RolloutEventKinds.Launched,
                subject: rollout.EnvKey, detail: $"{rollout.StepCount} step(s)",
                actorOid: rollout.LaunchedByOid, actorKind: rollout.LaunchedByKind, actorName: rollout.LaunchedByName,
                rolloutId: rollout.Id));

            if (rollout.FinishedAt is { } finishedAt && rollout.Status == RolloutStatus.Succeeded)
                entries.Add(Entry(finishedAt, TimelineCategories.Rollout, RolloutEventKinds.Succeeded,
                    subject: rollout.EnvKey, actorKind: ActorKinds.Coordinator, rolloutId: rollout.Id));
        }

        var rolloutIds = rollouts.Select(r => r.Id).ToList();
        var envByRollout = rollouts.ToDictionary(r => r.Id, r => r.EnvKey);

        // Launched and Succeeded are excluded: the rollout row above already owns them, and it is the
        // side that carries the environment and the launcher. Rendering both would put two entries on
        // one fact, at a bit-identical timestamp.
        var rolloutEvents = await db.RolloutEvents
            .Where(e => rolloutIds.Contains(e.RolloutId)
                        && e.Kind != RolloutEventKinds.Launched
                        && e.Kind != RolloutEventKinds.Succeeded)
            .OrderByDescending(e => e.At)
            .Take(limit)
            .AsNoTracking()
            .ToListAsync(ct);
        truncated |= rolloutEvents.Count == limit;

        foreach (var e in rolloutEvents)
            entries.Add(Entry(e.At, TimelineCategories.Rollout, e.Kind,
                subject: envByRollout.GetValueOrDefault(e.RolloutId),
                detail: e.PayloadJson,
                actorOid: e.ActorOid, actorKind: e.ActorKind, actorName: e.ActorName,
                rolloutId: e.RolloutId));

        // Step attempts are joined through RolloutStep.TaskId, not through the rollout's target. That
        // is what surfaces "this task was deployed inside somebody else's rollout" without anyone
        // having to ask for it -- a fact a purpose-built event log would not have produced.
        var attempts = await db.RolloutStepAttempts
            .Where(a => a.RolloutStep.TaskId == taskId)
            .OrderByDescending(a => a.At)
            .Take(limit)
            .Select(a => new
            {
                a.At, a.AttemptNo, a.Outcome, a.Message,
                a.RolloutStep.RolloutId,
                MrKey = a.RolloutStep.MergeRequest.ExternalId,
                RepoName = a.RolloutStep.MergeRequest.Repository.Name,
                TargetTaskId = a.RolloutStep.Rollout.TargetTaskId
            })
            .AsNoTracking()
            .ToListAsync(ct);
        truncated |= attempts.Count == limit;

        foreach (var a in attempts)
            entries.Add(Entry(a.At, TimelineCategories.Rollout, TimelineKinds.DeployAttempt,
                subject: $"{a.RepoName} !{a.MrKey}",
                detail: a.Message is null
                    ? $"#{a.AttemptNo} {a.Outcome}"
                    : $"#{a.AttemptNo} {a.Outcome}: {a.Message}",
                actorKind: ActorKinds.Coordinator,
                rolloutId: a.RolloutId,
                // Deployed as a prerequisite of another task's rollout.
                reassigned: a.TargetTaskId != taskId));

        // ---- merge ----------------------------------------------------------

        // Truncation is decided HERE, on the merged list, not only by the per-source caps above.
        // Two sources are uncapped by design -- merge requests emit up to three entries each and
        // rollouts one or two -- so the total can exceed the limit while every individual source
        // count is comfortably under it. Reporting only the per-source caps meant the final cut
        // discarded the oldest entries and still claimed complete coverage, which is precisely the
        // failure TimelineCoverageDto exists to prevent: the reader concludes nothing happened
        // before the point where the history simply stops.
        var ordered = Order(entries).ToList();
        truncated |= ordered.Count > limit;

        return new TaskTimelineDto(
            task.Id, task.ExternalId, IsArchived: false,
            task.FirstSeenAt, task.FirstSeenSource,
            new TimelineCoverageDto(await RecordingBeganAtAsync(ct), truncated, await AttributionIsSharedAsync(ct)),
            ordered.Take(limit).ToList());
    }

    /// <summary>
    /// Sorts newest first, with a total order rather than one that leaves ties to chance.
    /// </summary>
    /// <remarks>
    /// The tie-breakers are load-bearing. This codebase reads the clock once per unit of work and
    /// stamps every row in it with that value, so a rollout and its opening event and its first
    /// attempt routinely share an exact instant. Sorting on the timestamp alone would let those
    /// entries swap order between two identical requests, and a history that reorders itself while
    /// you refresh is one nobody trusts.
    /// </remarks>
    private static IEnumerable<TimelineEntryDto> Order(IEnumerable<TimelineEntryDto> entries) =>
        entries
            .OrderByDescending(e => e.At)
            .ThenBy(e => CategoryRank(e.Category))
            .ThenBy(e => e.Kind, StringComparer.Ordinal)
            .ThenBy(e => e.SubjectKey, StringComparer.Ordinal);

    private static int CategoryRank(string category) => category switch
    {
        TimelineCategories.Task => 0,
        TimelineCategories.MergeRequest => 1,
        TimelineCategories.Plan => 2,
        TimelineCategories.Rollout => 3,
        _ => 4
    };

    private sealed record PlanVersion(
        DateTime CreatedAt, int Version, string? ContentHash,
        string? CreatedByOid, string? CreatedByKind, string? CreatedByName);

    /// <summary>
    /// Collapses consecutive machine-authored plan versions that say the same thing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every ingestion event rebuilds every active plan, so a busy day produces dozens of versions
    /// that are byte-identical restatements. Listing them all buries the handful that changed
    /// something; hiding them all would hide the changes too. Collapsing consecutive equal hashes
    /// keeps every real change and reports the churn as a count.
    /// </para>
    /// <para>
    /// A version an operator asked for is never collapsed, even into an identical neighbour: "I
    /// pressed Recalculate and it changed nothing" is an answer somebody is looking for. A null hash
    /// (a version built before the column existed) never collapses either - treating unknown as a
    /// value would group versions that may well have differed.
    /// </para>
    /// </remarks>
    private static List<TimelineEntryDto> CollapsePlanVersions(List<PlanVersion> versions)
    {
        var result = new List<TimelineEntryDto>();

        for (int i = 0; i < versions.Count; i++)
        {
            var version = versions[i];
            var authored = version.CreatedByKind is not null;

            var repetitions = 1;
            var oldest = version;

            if (!authored && version.ContentHash is not null)
            {
                // The list is newest-first, so walking forward walks backwards in time.
                while (i + 1 < versions.Count
                       && versions[i + 1].CreatedByKind is null
                       && versions[i + 1].ContentHash == version.ContentHash)
                {
                    repetitions++;
                    oldest = versions[++i];
                }
            }

            result.Add(Entry(
                version.CreatedAt, TimelineCategories.Plan, TimelineKinds.PlanRecalculated,
                subject: version.Version.ToString(CultureInfo.InvariantCulture),
                actorOid: version.CreatedByOid, actorKind: version.CreatedByKind, actorName: version.CreatedByName,
                repetitions: repetitions,
                repeatedUntil: repetitions > 1 ? oldest.CreatedAt : null));
        }

        return result;
    }

    /// <summary>
    /// The earliest arrival this service ever stamped, used to date the line before which an empty
    /// timeline means "not recorded" rather than "nothing happened".
    /// </summary>
    private async Task<DateTime?> RecordingBeganAtAsync(CancellationToken ct) =>
        await db.Tasks.Where(t => t.FirstSeenAt != null).MinAsync(t => t.FirstSeenAt, ct);

    /// <summary>
    /// True when every operator shares one identity, which makes attribution on this page decorative.
    /// </summary>
    /// <remarks>
    /// Detected from the data rather than from configuration: if more than one distinct launcher oid
    /// has ever been recorded, identities are real. One oid across many rollouts by different people
    /// is what the Local auth provider produces, and an operator reading the page deserves to be told
    /// that rather than concluding one colleague did everything.
    /// </remarks>
    private async Task<bool> AttributionIsSharedAsync(CancellationToken ct)
    {
        // Every actor column the page renders, not launches alone: a deployment where one person
        // launches but several pin statuses or recalculate plans has real attribution, and warning
        // that it does not would teach an operator to distrust names that are accurate.
        var oids = await db.Rollouts.Where(r => r.LaunchedByOid != null).Select(r => r.LaunchedByOid)
            .Union(db.RolloutPlans.Where(p => p.CreatedByOid != null).Select(p => p.CreatedByOid))
            .Union(db.MergeRequestStatusChanges.Where(c => c.ActorOid != null).Select(c => c.ActorOid))
            .Distinct()
            .Take(2)
            .CountAsync(ct);

        // Still a heuristic, and deliberately a conservative one: a genuine one-person team looks
        // identical to a shared identity from the data alone. Erring towards silence is right --
        // a false warning that attribution is meaningless is worse than no warning, because it
        // discredits names that are in fact correct. The definitive signal is the auth provider,
        // which the page reads separately.
        return oids == 1 && await db.Rollouts.CountAsync(r => r.LaunchedByOid != null, ct) > 1;
    }

    /// <summary>What survives for a task that has been archived: its own two moments, and nothing else.</summary>
    private async Task<List<TimelineEntryDto>> ArchivedEntriesAsync(
        Guid taskId, string externalId, DateTime? closedAt, DateTime archivedAt, CancellationToken ct)
    {
        var entries = new List<TimelineEntryDto>();

        var mrs = await archiveDb.ArchivedMergeRequests
            .Where(m => m.TaskExternalId == externalId)
            .Select(m => new { m.ExternalId, m.RepositoryName, m.CreatedAt, m.MergedAt, m.ClosedAt })
            .AsNoTracking()
            .ToListAsync(ct);

        foreach (var mr in mrs)
        {
            var subject = $"{mr.RepositoryName} !{mr.ExternalId}";
            entries.Add(Entry(mr.CreatedAt, TimelineCategories.MergeRequest, TimelineKinds.MrOpened,
                subject: subject, clock: TimelineClockSources.External));
            if (mr.MergedAt is { } mergedAt)
                entries.Add(Entry(mergedAt, TimelineCategories.MergeRequest, TimelineKinds.MrMerged,
                    subject: subject, clock: TimelineClockSources.External));
            if (mr.ClosedAt is { } mrClosed)
                entries.Add(Entry(mrClosed, TimelineCategories.MergeRequest, TimelineKinds.MrClosed,
                    subject: subject, clock: TimelineClockSources.External));
        }

        if (closedAt is { } taskClosed)
            entries.Add(Entry(taskClosed, TimelineCategories.Task, TimelineKinds.TaskClosed, subject: externalId,
                actorKind: ActorKinds.Webhook, clock: TimelineClockSources.External));

        return [.. Order(entries)];
    }

    private static TimelineEntryDto Entry(
        DateTime at,
        string category,
        string kind,
        string? subject = null,
        string? detail = null,
        string? actorOid = null,
        string? actorKind = null,
        string? actorName = null,
        string clock = TimelineClockSources.Ours,
        int repetitions = 1,
        DateTime? repeatedUntil = null,
        Guid? rolloutId = null,
        Guid? mergeRequestId = null,
        bool reassigned = false) =>
        new(at, category, kind, actorOid, actorKind, actorName, subject, detail, clock,
            repetitions, repeatedUntil, rolloutId, mergeRequestId, reassigned);
}
