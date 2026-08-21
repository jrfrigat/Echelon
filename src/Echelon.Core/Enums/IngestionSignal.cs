namespace Echelon.Core.Enums;

/// <summary>
/// A kind of change arriving from outside, however it arrived.
/// </summary>
/// <remarks>
/// This is what answers "did anything actually happen": a webhook and a poll produce the same signals,
/// so the count and the last-seen time say whether the tracker and the VCS are being heard at all -
/// which no worker's own status can tell you, since a sweep that finds nothing looks exactly like a
/// sweep that cannot see anything.
/// </remarks>
public enum IngestionSignal
{
    /// <summary>A task was created locally from a tracker issue.</summary>
    TaskCreated = 0,

    /// <summary>A task's status changed in the tracker.</summary>
    TaskStatusChanged = 1,

    /// <summary>A task was re-read from the tracker, links included.</summary>
    TaskSynced = 2,

    /// <summary>A merge request was opened, or seen open for the first time.</summary>
    MergeRequestOpened = 3,

    /// <summary>A merge request's status changed.</summary>
    MergeRequestStatusChanged = 4,

    /// <summary>Branches were observed in a repository.</summary>
    BranchesObserved = 5,

    /// <summary>A rollout plan was rebuilt.</summary>
    PlanRecalculated = 6
}
