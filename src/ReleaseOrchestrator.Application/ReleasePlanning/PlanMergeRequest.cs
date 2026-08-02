using ReleaseOrchestrator.Core.Enums;

namespace ReleaseOrchestrator.Application.ReleasePlanning;

/// <summary>
/// A deployable merge request, reduced to exactly what deciding its order needs.
/// </summary>
/// <param name="Id">The merge request. Stages are lists of these.</param>
/// <param name="TaskId">
/// Its task, if a branch named one. Merge requests of the same task are not ordered by task
/// dependency (they share a task); they order each other only via the repository-ordering policy.
/// </param>
/// <param name="DependsOnTaskIds">Tasks this one waits on. Every merge request of those deploys first.</param>
/// <param name="ChildTaskIds">
/// The subtasks of this merge request's task, which it also waits on: a parent is the umbrella over
/// the concrete work its children carry, so the children deploy first and the parent last.
/// <para>
/// Carried separately from <see cref="DependsOnTaskIds"/> rather than merged into it, for one
/// unglamorous reason: the two come from different navigations, and EF cannot translate a projection
/// that concatenates two collection subqueries — it throws at query time, which is how this arrived
/// here. Separate is also what makes the hierarchy's direction assertable without a database, and
/// the direction is the part that silently inverts.
/// </para>
/// </param>
/// <param name="RepositoryId">Its repository, used to derive repository-ordering edges.</param>
/// <param name="RepositoryDependsOn">Repositories this one's repository deploys after, and how firmly.</param>
/// <remarks>
/// The planner used to take the <c>MergeRequest</c> entity and read its navigations, which meant
/// the data it needed was stated in a comment — "callers must load Task.Dependencies and
/// Repository.DependsOn" — and enforced by nothing. Miss an <c>Include</c> and the navigation is
/// not an error, it is empty: no exception, no warning, an ordering built from half the
/// constraints that looks exactly like a correct one.
///
/// Naming the input instead moves that from a comment to the type. A caller cannot supply a plan
/// input without supplying its dependencies, because there is no field to leave unfilled.
/// </remarks>
public record PlanMergeRequest(
    Guid Id,
    Guid? TaskId,
    IReadOnlyList<Guid> DependsOnTaskIds,
    IReadOnlyList<Guid> ChildTaskIds,
    Guid RepositoryId,
    IReadOnlyList<PlanRepositoryLink> RepositoryDependsOn)
{
    /// <summary>
    /// Every task this one deploys after, whatever the reason — declared dependencies and the
    /// hierarchy's children alike.
    /// </summary>
    /// <remarks>
    /// The two are kept apart in storage and in the projection because they arrive by different
    /// routes, but the planner has exactly one notion of "goes first", and it reads it here. Ordering
    /// code that reached for <see cref="DependsOnTaskIds"/> alone would honour declared dependencies
    /// and quietly ignore the hierarchy.
    /// </remarks>
    public IEnumerable<Guid> PrerequisiteTaskIds => DependsOnTaskIds.Concat(ChildTaskIds);

    /// <summary>
    /// Replaces the prerequisites read from the tracker with the ones the wait policy admits.
    /// </summary>
    /// <param name="prerequisites">The resolved set, from <see cref="TaskWaitGraph"/>.</param>
    /// <remarks>
    /// Both the closure and the edges between merge requests have to come from the same resolved
    /// adjacency, or the policy holds for one and not the other. It did not, and the failure was
    /// silent in the worst way: turning off a wait, or asking for subtasks-before-linked, changed
    /// which tasks the plan covered and left the deploy order exactly as it was — because these two
    /// lists were still read straight off the tracker's navigations further down.
    ///
    /// Collapsed into <see cref="DependsOnTaskIds"/> because the split exists only to get two
    /// collection subqueries past EF; once resolved, the planner has one notion of "goes first".
    /// </remarks>
    public PlanMergeRequest WithPrerequisites(IReadOnlyList<Guid> prerequisites) =>
        this with { DependsOnTaskIds = prerequisites, ChildTaskIds = [] };
}

/// <summary>"Deploy after every merge request in <paramref name="ToRepositoryId"/>", and how firmly.</summary>
/// <param name="ToRepositoryId">The repository that must go first.</param>
/// <param name="Type">Hard links never yield; soft ones are dropped first to break a cycle.</param>
public record PlanRepositoryLink(Guid ToRepositoryId, StackDependencyType Type);
