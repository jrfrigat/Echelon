namespace Echelon.Application.DTOs;

/// <summary>
/// A per-task rollout plan: the tree of the target's dependency closure plus the merge-request-level
/// execution waves.
/// </summary>
/// <remarks>
/// <see cref="Nodes"/> is the presentation tree (target and its prerequisite tasks); <see cref="Waves"/>
/// is the execution order, which is a merge-request property -- a task's merge requests can land in
/// different waves.
/// </remarks>
public record RolloutPlanDto(
    Guid Id,
    Guid TargetTaskId,
    string TargetTaskKey,
    int Version,
    string Source,
    string Status,
    bool IsActive,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    IReadOnlyList<PlanTaskNodeDto> Nodes,
    IReadOnlyList<PlanWaveDto> Waves,
    IReadOnlyList<PlanConflictDto> Conflicts);
