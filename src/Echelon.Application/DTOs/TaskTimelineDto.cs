namespace Echelon.Application.DTOs;

/// <summary>
/// Everything that happened to one task, in order.
/// </summary>
/// <param name="TaskId">The task.</param>
/// <param name="TaskExternalId">Its tracker key.</param>
/// <param name="IsArchived">True when the task itself has been archived; the entries then come from the archive.</param>
/// <param name="FirstSeenAt">When this service first stored it, or null when it predates arrival recording.</param>
/// <param name="FirstSeenSource">How it arrived, or null when the creating path knew of no source.</param>
/// <param name="Coverage">What this answer does NOT cover. Read it before trusting an absence.</param>
/// <param name="Entries">The events, newest first.</param>
public record TaskTimelineDto(
    Guid TaskId,
    string TaskExternalId,
    bool IsArchived,
    DateTime? FirstSeenAt,
    string? FirstSeenSource,
    TimelineCoverageDto Coverage,
    IReadOnlyList<TimelineEntryDto> Entries);

/// <summary>
/// The limits of what the timeline can say, carried as data rather than left for the reader to guess.
/// </summary>
/// <param name="RecordingBeganAt">
/// The earliest arrival timestamp this service has recorded, used to draw the line before which
/// silence means "not recorded" rather than "nothing happened". Null when nothing has been recorded yet.
/// </param>
/// <param name="Truncated">True when any source hit its row cap, so entries are missing from the middle.</param>
/// <param name="AttributionIsShared">
/// True when the deployment issues one identity to every operator, which makes every "who" on this
/// page the same person regardless of who acted.
/// </param>
/// <remarks>
/// A timeline that renders an incomplete picture as though it were complete is worse than one that
/// refuses to render: the reader draws conclusions from absences. These flags exist so the UI can
/// say which absences are meaningful.
/// </remarks>
public record TimelineCoverageDto(
    DateTime? RecordingBeganAt,
    bool Truncated,
    bool AttributionIsShared);

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

/// <summary>The vocabulary of <see cref="TimelineEntryDto.Category"/>.</summary>
public static class TimelineCategories
{
    /// <summary>The task itself: arrival, closure.</summary>
    public const string Task = "task";

    /// <summary>A merge request: opened, status changed, merged, closed.</summary>
    public const string MergeRequest = "mergeRequest";

    /// <summary>The rollout plan: recalculated.</summary>
    public const string Plan = "plan";

    /// <summary>A rollout: launched, step attempts, paused, cancelled, finished.</summary>
    public const string Rollout = "rollout";
}

/// <summary>The vocabulary of <see cref="TimelineEntryDto.Kind"/>. The UI maps each to a resource key.</summary>
public static class TimelineKinds
{
    /// <summary>The task was first stored by this service.</summary>
    public const string TaskFirstSeen = "TaskFirstSeen";

    /// <summary>The task reached a closed status in the tracker.</summary>
    public const string TaskClosed = "TaskClosed";

    /// <summary>A merge request for this task was opened.</summary>
    public const string MrOpened = "MrOpened";

    /// <summary>A merge request's status changed.</summary>
    public const string MrStatusChanged = "MrStatusChanged";

    /// <summary>A merge request was merged.</summary>
    public const string MrMerged = "MrMerged";

    /// <summary>A merge request was closed without merging.</summary>
    public const string MrClosed = "MrClosed";

    /// <summary>The plan was rebuilt.</summary>
    public const string PlanRecalculated = "PlanRecalculated";

    /// <summary>A deploy attempt on one step.</summary>
    public const string DeployAttempt = "DeployAttempt";
}

/// <summary>The vocabulary of <see cref="TimelineEntryDto.ClockSource"/>.</summary>
public static class TimelineClockSources
{
    /// <summary>Stamped by this service.</summary>
    public const string Ours = "ours";

    /// <summary>
    /// Stamped by the VCS or the tracker, so it is not strictly comparable with our own timestamps.
    /// </summary>
    /// <remarks>
    /// Marked rather than silently mixed: a merge request's creation time means "when GitLab opened
    /// it" on the poll path and "when we received it" on the webhook path, with nothing in the row
    /// to say which. Ordering across the two is approximate and the reader is told so.
    /// </remarks>
    public const string External = "external";
}
