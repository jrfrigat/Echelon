namespace Echelon.Application.DTOs;

/// <summary>
/// One thing that happened.
/// </summary>
/// <param name="At">When. Always UTC.</param>
/// <param name="Category">Coarse grouping for filtering and iconography: task, mergeRequest, plan, rollout.</param>
/// <param name="Kind">
/// What happened, as a stable token the UI maps to a localized sentence. Never a rendered sentence:
/// the API stays culture-neutral, and a caller that is not the PWA gets something it can switch on.
/// </param>
/// <param name="ActorOid">Who, when a person or service did it. Null for machine paths.</param>
/// <param name="ActorKind">The kind of actor, so "no id" and "no person" are distinguishable.</param>
/// <param name="ActorName">The actor's captured display name, when one was recorded.</param>
/// <param name="SubjectKey">What it was about: a merge-request key, an environment key, a plan version.</param>
/// <param name="Detail">Secondary text - an error message, a status transition, a count.</param>
/// <param name="ClockSource">
/// Whose clock stamped <paramref name="At"/>: <c>ours</c>, or <c>external</c> when the timestamp came
/// from the VCS or tracker and may not be comparable with the rest.
/// </param>
/// <param name="Repetitions">
/// How many consecutive identical events this entry stands for. 1 for an ordinary entry; higher when
/// machine churn was collapsed.
/// </param>
/// <param name="RepeatedUntil">The last timestamp in a collapsed run, when <paramref name="Repetitions"/> exceeds 1.</param>
/// <param name="RolloutId">The rollout this belongs to, so the UI can link through to it.</param>
/// <param name="MergeRequestId">The merge request this concerns.</param>
/// <param name="IsReassigned">
/// True when this entry is about a merge request that has since been re-linked to another task. The
/// history is still shown - it happened while the merge request belonged here - but it is marked.
/// </param>
public record TimelineEntryDto(
    DateTime At,
    string Category,
    string Kind,
    string? ActorOid,
    string? ActorKind,
    string? ActorName,
    string? SubjectKey,
    string? Detail,
    string ClockSource,
    int Repetitions,
    DateTime? RepeatedUntil,
    Guid? RolloutId,
    Guid? MergeRequestId,
    bool IsReassigned);
