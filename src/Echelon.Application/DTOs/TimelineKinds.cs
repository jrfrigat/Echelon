namespace Echelon.Application.DTOs;

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
