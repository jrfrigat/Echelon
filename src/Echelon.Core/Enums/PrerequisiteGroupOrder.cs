namespace Echelon.Core.Enums;

/// <summary>
/// When a task waits on both its subtasks and its declared dependencies, whether one whole group
/// deploys before the other.
/// </summary>
/// <remarks>
/// <para>
/// This is a preference about grouping, not a constraint the tracker stated. The two kinds of
/// prerequisite are unrelated to each other by default: nothing says a subtask must deploy before a
/// linked task, so by default (<see cref="Together"/>) they are simply both waited on and the graph
/// orders them by whatever real constraints exist between their merge requests.
/// </para>
/// <para>
/// Choosing a group order adds edges the tracker never declared, between tasks that may have nothing
/// to do with each other. That can make an otherwise-acyclic graph cyclic - the planner then drops
/// an edge and records the conflict, as it does for any other cycle. Which is why the default is to
/// impose nothing.
/// </para>
/// </remarks>
public enum PrerequisiteGroupOrder
{
    /// <summary>No grouping. Both kinds are waited on, ordered only by real constraints. The default.</summary>
    Together = 1,

    /// <summary>Every subtask deploys before any declared dependency does.</summary>
    SubtasksFirst = 2,

    /// <summary>Every declared dependency deploys before any subtask does.</summary>
    LinkedFirst = 3
}
