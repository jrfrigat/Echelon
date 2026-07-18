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
    Guid RepositoryId,
    IReadOnlyList<PlanRepositoryLink> RepositoryDependsOn);

/// <summary>"Deploy after every merge request in <paramref name="ToRepositoryId"/>", and how firmly.</summary>
/// <param name="ToRepositoryId">The repository that must go first.</param>
/// <param name="Type">Hard links never yield; soft ones are dropped first to break a cycle.</param>
public record PlanRepositoryLink(Guid ToRepositoryId, StackDependencyType Type);
