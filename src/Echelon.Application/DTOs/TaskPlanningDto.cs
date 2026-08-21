namespace Echelon.Application.DTOs;

/// <summary>One task's departures from the installation defaults, and its explicit orderings.</summary>
/// <param name="WaitForSubtasks">The task's answer, or null to inherit.</param>
/// <param name="WaitForLinked">The task's answer, or null to inherit.</param>
/// <param name="PrerequisiteGroupOrder">The task's answer, or null/empty to inherit.</param>
/// <param name="PrerequisiteOrder">The order to wait on prerequisite tasks in; empty for none.</param>
/// <param name="MergeRequestOrder">The order to deploy this task's merge requests in; empty for none.</param>
public record TaskPlanningDto(
    bool? WaitForSubtasks,
    bool? WaitForLinked,
    string? PrerequisiteGroupOrder,
    IReadOnlyList<Guid>? PrerequisiteOrder,
    IReadOnlyList<Guid>? MergeRequestOrder);
