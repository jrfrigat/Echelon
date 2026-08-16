namespace Echelon.Core.Enums;

/// <summary>Whether a per-task rollout plan is ready to launch from.</summary>
public enum PlanStatus
{
    /// <summary>Being assembled or edited; not yet the plan of record.</summary>
    Draft = 1,

    /// <summary>The active plan for its target task.</summary>
    Ready = 2,

    /// <summary>The atlas changed under it; a recalculation is due.</summary>
    Stale = 3
}
