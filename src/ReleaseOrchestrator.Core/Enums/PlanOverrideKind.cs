namespace ReleaseOrchestrator.Core.Enums;

/// <summary>
/// An operator edit to a per-task plan, stored as a delta so it survives a recalculation.
/// </summary>
/// <remarks>
/// The plan is a projection of the atlas; recomputing it would discard hand edits unless they are
/// replayed. Storing edits as deltas (not as a frozen result) is what lets the plan track the atlas
/// and keep the operator's intent (docs/issues/006-per-task-planning.md).
/// </remarks>
public enum PlanOverrideKind
{
    /// <summary>Add a "deploy A before B" ordering edge the derivation did not produce.</summary>
    AddEdge = 1,

    /// <summary>Drop a derived ordering edge.</summary>
    RemoveEdge = 2,

    /// <summary>Force a merge request into the plan.</summary>
    IncludeMr = 3,

    /// <summary>Force a merge request out of the plan.</summary>
    ExcludeMr = 4,

    /// <summary>Pin the order of a merge request within its task.</summary>
    SetIntraTaskOrder = 5
}
