namespace Echelon.Application.DTOs;

/// <summary>A task in the plan tree, with the merge requests attached to it.</summary>
public record PlanTaskNodeDto(
    Guid TaskId,
    string TaskKey,
    string TaskTitle,
    bool IsTarget,
    IReadOnlyList<Guid> DependsOnTaskIds,
    IReadOnlyList<PlanItemDto> Items);
