using ReleaseOrchestrator.Application.DTOs;
using ReleaseOrchestrator.Core.Parsing;
using ReleaseOrchestrator.Infrastructure.Persistence;
using ReleaseOrchestrator.Infrastructure.Persistence.Models;

namespace ReleaseOrchestrator.Infrastructure.Audit;

/// <summary>
/// Applies an observed label set to a merge request and records the change into the journal the
/// readiness history reads.
/// </summary>
/// <remarks>
/// <para>
/// The label counterpart of <see cref="MergeRequestStatusJournal"/>, with one deliberate difference:
/// it <b>mutates</b> <see cref="MergeRequest.Labels"/> as well as writing the journal row. The status
/// journal only records, because the caller already computed and assigned the status; here the caller
/// has raw labels, and canonicalisation (<see cref="LabelSet.Canonical"/>) is the thing that decides
/// both what to store and whether anything changed. Doing it in one place is what keeps the three
/// ingestion paths — opened webhook, status webhook, poll — from each normalising slightly
/// differently and disagreeing about whether two label sets are the same.
/// </para>
/// <para>
/// Like the status journal it <b>adds and never saves</b>: the row joins the caller's unit of work
/// and commits with the label change or not at all.
/// </para>
/// </remarks>
public static class MergeRequestLabelJournal
{
    /// <summary>
    /// Observed on an inbound observation of the merge request.
    /// </summary>
    /// <remarks>
    /// Both the webhook front door and the poller deliver labels through the same <c>MrOpened</c>
    /// message, and their <c>Source</c> is spelled identically (<c>"{providerType}/{connectionName}"</c>),
    /// so this path cannot tell push from pull. It records the observation under one cause rather than
    /// guessing. A distinct <c>poll</c> cause can be added when a change actually carries that
    /// distinction; inventing it here would be a label the data does not support.
    /// </remarks>
    public const string CauseWebhook = MergeRequestStatusJournal.CauseWebhook;

    /// <summary>
    /// Canonicalises <paramref name="observedLabels"/>, stores them on the merge request, and records
    /// the transition — unless the set did not actually change.
    /// </summary>
    /// <param name="db">The context whose unit of work this row joins.</param>
    /// <param name="mergeRequest">The merge request; its <see cref="MergeRequest.Labels"/> is updated in place.</param>
    /// <param name="observedLabels">The labels as the provider reported them, in any form.</param>
    /// <param name="cause">One of the <c>Cause*</c> constants.</param>
    /// <param name="actor">Who did it; <see cref="ActorRef.System"/> for the machine paths.</param>
    /// <param name="at">When, in UTC.</param>
    /// <returns>True when the set changed and a row was written; false on a no-op.</returns>
    /// <remarks>
    /// A no-op when the canonical set is unchanged. Deliveries repeat and a re-sent open event
    /// reasserts the same labels; without this guard the history would fill with entries for changes
    /// that did not happen. A newly observed merge request with no labels is also a no-op — its stored
    /// set starts empty, so "no labels" to "no labels" records nothing — while one that arrives
    /// carrying labels records a transition from empty, which is the truth: it gained them.
    /// </remarks>
    public static bool Apply(
        AppDbContext db,
        MergeRequest mergeRequest,
        IEnumerable<string?>? observedLabels,
        string cause,
        ActorRef actor,
        DateTime at)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(mergeRequest);
        ArgumentNullException.ThrowIfNull(actor);

        var to = LabelSet.Canonical(observedLabels);
        var from = mergeRequest.Labels;

        // Ordinal: Labels is already the canonical form, and canonicalisation lower-cases, so this is
        // a byte comparison of two normalised strings, not a culture-aware one.
        if (string.Equals(from, to, StringComparison.Ordinal))
            return false;

        mergeRequest.Labels = to;

        db.MergeRequestLabelChanges.Add(new MergeRequestLabelChange
        {
            Id = Guid.NewGuid(),
            MergeRequestId = mergeRequest.Id,
            TaskId = mergeRequest.TaskId,
            MergeRequestExternalId = mergeRequest.ExternalId,
            FromLabels = from,
            ToLabels = to,
            Cause = cause,
            ActorOid = actor.Oid,
            ActorKind = actor.IsPerson || actor.Kind == ActorKinds.Service ? actor.Kind : null,
            ActorName = actor.DisplayName,
            At = at
        });

        return true;
    }
}
