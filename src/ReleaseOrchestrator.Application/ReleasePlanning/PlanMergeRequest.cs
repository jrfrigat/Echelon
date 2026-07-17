using ReleaseOrchestrator.Core.Enums;

namespace ReleaseOrchestrator.Application.ReleasePlanning;

/// <summary>
/// A deployable merge request, reduced to exactly what deciding its order needs.
/// </summary>
/// <param name="Id">The merge request. Stages are lists of these.</param>
/// <param name="TaskId">Its task, if a branch named one. Merge requests of the same task never order each other.</param>
/// <param name="DependsOnTaskIds">Tasks this one waits on. Every merge request of those deploys first.</param>
/// <param name="Stacks">The stacks its repository belongs to, each with the stacks it waits on.</param>
/// <remarks>
/// The planner used to take the <c>MergeRequest</c> entity and read its navigations, which meant
/// the data it needed was stated in a comment — "callers must load Task.Dependencies and
/// Repository.RepositoryStacks.Stack.DependentOn" — and enforced by nothing. Miss an
/// <c>Include</c> and the navigation is not an error, it is empty: no exception, no warning, an
/// ordering built from half the constraints that looks exactly like a correct one.
///
/// Naming the input instead moves that from a comment to the type. A caller cannot supply a plan
/// input without supplying its dependencies, because there is no field to leave unfilled.
/// </remarks>
public record PlanMergeRequest(
    Guid Id,
    Guid? TaskId,
    IReadOnlyList<Guid> DependsOnTaskIds,
    IReadOnlyList<PlanRepositoryStack> Stacks);

/// <summary>A stack a repository belongs to, with the stacks that stack waits on.</summary>
/// <param name="StackId">The stack.</param>
/// <param name="DependsOn">Stacks it deploys after.</param>
public record PlanRepositoryStack(Guid StackId, IReadOnlyList<PlanStackLink> DependsOn);

/// <summary>"Deploy after <paramref name="ToStackId"/>", and how firmly.</summary>
/// <param name="ToStackId">The stack that must go first.</param>
/// <param name="Type">Hard links never yield; soft ones are dropped first to break a cycle.</param>
public record PlanStackLink(Guid ToStackId, StackDependencyType Type);
