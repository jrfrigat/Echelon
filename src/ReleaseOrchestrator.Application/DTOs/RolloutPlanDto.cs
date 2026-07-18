namespace ReleaseOrchestrator.Application.DTOs;

/// <summary>A task as it appears in the tasks list -- what an operator might roll out.</summary>
public record TaskListItemDto(
    Guid Id,
    string ExternalId,
    string Title,
    string Status,
    int MergeRequestCount,
    bool HasActivePlan);

/// <summary>
/// A per-task rollout plan: the tree of the target's dependency closure plus the merge-request-level
/// execution waves.
/// </summary>
/// <remarks>
/// <see cref="Nodes"/> is the presentation tree (target and its prerequisite tasks); <see cref="Waves"/>
/// is the execution order, which is a merge-request property -- a task's merge requests can land in
/// different waves (docs/issues/004-target-architecture.md).
/// </remarks>
public record RolloutPlanDto(
    Guid Id,
    Guid TargetTaskId,
    string TargetTaskKey,
    string Version,
    string Source,
    string Status,
    bool IsActive,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    IReadOnlyList<PlanTaskNodeDto> Nodes,
    IReadOnlyList<PlanWaveDto> Waves,
    IReadOnlyList<PlanConflictDto> Conflicts);

/// <summary>A task in the plan tree, with the merge requests attached to it.</summary>
public record PlanTaskNodeDto(
    Guid TaskId,
    string TaskKey,
    string TaskTitle,
    bool IsTarget,
    IReadOnlyList<Guid> DependsOnTaskIds,
    IReadOnlyList<PlanItemDto> Items);

/// <summary>A merge request in the plan, with the wave it deploys in.</summary>
public record PlanItemDto(
    Guid MergeRequestId,
    string MrExternalId,
    string RepositoryName,
    string SourceBranch,
    string TargetBranch,
    string MrStatus,
    int Wave);

/// <summary>One execution wave: merge requests that deploy in parallel.</summary>
public record PlanWaveDto(int Sequence, IReadOnlyList<Guid> MergeRequestIds);
