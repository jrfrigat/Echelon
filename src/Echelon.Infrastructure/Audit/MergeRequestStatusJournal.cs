using Echelon.Application.DTOs;
using Echelon.Core.Enums;
using Echelon.Infrastructure.Persistence;
using Echelon.Infrastructure.Persistence.Models;

namespace Echelon.Infrastructure.Audit;

/// <summary>
/// Records merge-request status transitions into the journal the task timeline reads.
/// </summary>
/// <remarks>
/// <para>
/// One helper rather than four hand-written inserts, because there are four places a status is
/// assigned - the label consumer, the status webhook, the poller, and the manual pin - and a fifth
/// will be added eventually. Anything that writes <see cref="MergeRequest.Status"/> and does not
/// come through here leaves a hole in the history that nothing reports; the source-scanning test in
/// the suite is what keeps that honest.
/// </para>
/// <para>
/// It <b>adds and never saves</b>. The row joins whatever unit of work the caller is already in, so
/// it commits with the status change or not at all. A journal that could persist while the change it
/// describes rolled back would be worse than no journal - it would be a confident record of
/// something that never happened.
/// </para>
/// </remarks>
public static class MergeRequestStatusJournal
{
    /// <summary>Caused by a merge request gaining or losing the deploy-readiness label.</summary>
    public const string CauseLabel = "label";

    /// <summary>Caused by an inbound status webhook from the VCS.</summary>
    public const string CauseWebhook = "webhook";

    /// <summary>Noticed by the polling ingestion path.</summary>
    public const string CausePoll = "poll";

    /// <summary>An operator set the status by hand.</summary>
    public const string CauseManualPin = "manual-pin";

    /// <summary>
    /// Records a transition, unless there was not one.
    /// </summary>
    /// <param name="db">The context whose unit of work this row joins.</param>
    /// <param name="mergeRequest">The merge request being changed. Read for its identity, not modified.</param>
    /// <param name="from">The status before, or null when the row is being created.</param>
    /// <param name="to">The status after.</param>
    /// <param name="cause">One of the <c>Cause*</c> constants.</param>
    /// <param name="actor">Who did it; <see cref="ActorRef.System"/> for the machine paths.</param>
    /// <param name="at">When, in UTC.</param>
    /// <remarks>
    /// A no-op when <paramref name="from"/> equals <paramref name="to"/>. Deliveries repeat - Rebus
    /// is at-least-once and Yandex webhooks carry no event id to deduplicate on - and a redelivery
    /// that reasserts the current status is not a transition. Without this guard the timeline would
    /// fill with entries for changes that did not occur, which is the one failure mode that makes an
    /// audit worse than useless.
    /// </remarks>
    public static void Record(
        AppDbContext db,
        MergeRequest mergeRequest,
        MergeRequestStatus? from,
        MergeRequestStatus to,
        string cause,
        ActorRef actor,
        DateTime at)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(mergeRequest);
        ArgumentNullException.ThrowIfNull(actor);

        if (from == to) return;

        db.MergeRequestStatusChanges.Add(new MergeRequestStatusChange
        {
            Id = Guid.NewGuid(),
            MergeRequestId = mergeRequest.Id,
            TaskId = mergeRequest.TaskId,
            MergeRequestExternalId = mergeRequest.ExternalId,
            FromStatus = from?.ToString(),
            ToStatus = to.ToString(),
            Cause = cause,
            ActorOid = actor.Oid,
            // Null rather than "system" for the machine paths: the column reads as "nobody", which is
            // the truth, and Cause already says which machine path it was.
            ActorKind = actor.IsPerson || actor.Kind == ActorKinds.Service ? actor.Kind : null,
            ActorName = actor.DisplayName,
            At = at
        });
    }
}
