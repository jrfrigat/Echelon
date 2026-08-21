using Echelon.Core.Enums;

namespace Echelon.Application.DTOs;
/// <summary>
/// One piece of deployable work: a task's presence in a repository, and what carries it.
/// </summary>
/// <remarks>
/// The row is (task, repository), not the merge request. A connector reports that a task has work
/// somewhere; before a merge request is raised the work is a branch, and the task is the same either
/// way.
/// </remarks>
/// <param name="Kind">What the work currently rides in.</param>
/// <param name="TaskKey">The task the linking rule matched, or null when it matched none.</param>
/// <param name="RepositoryName">The repository the work is in.</param>
/// <param name="ConnectionName">The connection that reported it.</param>
/// <param name="Carrier">The merge request's id, or the branch's name.</param>
/// <param name="Branch">The branch either way - a merge request's source branch, or the branch itself.</param>
/// <param name="State">
/// <c>New</c> for a branch nothing has raised, otherwise the merge request's status. A string rather
/// than an enum because it is the union of the two: a branch has no merge-request status, and adding
/// "New" to <see cref="MergeRequestStatus"/> would put a branch state on the merge request.
/// </param>
/// <param name="IsStatusManual">Whether an operator pinned the status by hand.</param>
/// <param name="Labels">Labels carried, for reading against a readiness rule.</param>
/// <param name="PipelineResult">The latest pipeline result, when the source reports one.</param>
/// <param name="Readiness">Judgement for the named environment, or null when none was named or none is possible.</param>
/// <param name="At">When the work first appeared.</param>
public record WorkItemDto(
    WorkItemKind Kind,
    string? TaskKey,
    string RepositoryName,
    string ConnectionName,
    string Carrier,
    string Branch,
    string State,
    bool IsStatusManual,
    IReadOnlyList<string> Labels,
    string? PipelineResult,
    WorkItemReadinessDto? Readiness,
    DateTime At);
