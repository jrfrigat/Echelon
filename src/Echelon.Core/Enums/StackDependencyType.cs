namespace Echelon.Core.Enums;

/// <summary>
/// How binding a repository-ordering link is - and therefore which link the planner sacrifices
/// first when the ordering graph turns out to be cyclic.
/// </summary>
/// <remarks>
/// The planner can always produce an order, so the only real question a cycle asks is what to give
/// up. This is the answer, declared per link by the operator rather than guessed at break time.
/// The mapping onto the planner's drop precedence lives in <c>PlanEdgeKind</c>; every dropped link
/// is reported as a conflict, whichever kind it was.
/// </remarks>
public enum StackDependencyType
{
    /// <summary>
    /// A real ordering requirement: deploying out of this order breaks something. Never dropped to
    /// break a cycle, so an all-hard cycle is reported as unsatisfiable rather than quietly resolved.
    /// </summary>
    Hard = 1,

    /// <summary>
    /// A preference. Honoured when it can be, and the first thing dropped when a cycle forces a
    /// choice - which is also why a soft link is excluded from the constraints an operator-edited
    /// plan is vetted against.
    /// </summary>
    Soft = 2
}
