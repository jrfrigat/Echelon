namespace Echelon.Application.DTOs;

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
