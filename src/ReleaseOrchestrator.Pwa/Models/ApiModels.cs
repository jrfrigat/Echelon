namespace ReleaseOrchestrator.Pwa.Models;

// Mirrors ReleaseOrchestrator.Application.DTOs. Enum-valued fields are strings here, which
// only works because the API registers JsonStringEnumConverter — keep the two in step.

/// <summary>A dependency the plan could not honour; shown to operators as a warning. Shared by the per-task rollout plan.</summary>
public record PlanConflictDto(
    string Kind, Guid FromMergeRequestId, Guid ToMergeRequestId, string Reason);

public record VcsConnectionDto(
    Guid Id, string Name, string VcsType, string ApiUrl,
    string? ReadyForDeployLabel = null, string? ConnectionName = null);

public record TrackerConnectionDto(Guid Id, string Name, string TrackerType, string ApiUrl, string? OrgId);

public record RepositoryDto(Guid Id, string Name, string ExternalId, Guid ConnectionId, string ConnectionName);

public record MrDto(
    Guid Id, string ExternalId, string SourceBranch, string TargetBranch,
    string Status, DateTime CreatedAt, Guid RepositoryId,
    string RepositoryName, string ConnectionName, string? TaskExternalId,
    bool IsStatusManual = false);

public record PagedResult<T>(int Total, int Page, int PageSize, List<T> Items);

public record PermissionClaimDto(Guid Id, string Name);
public record GroupMappingDto(Guid Id, string AdGroupSid, string ClaimName);

// ---- per-task rollout plans (mirrors Application.DTOs.RolloutPlanDto) ----

public record TaskListItemDto(
    Guid Id, string ExternalId, string Title, string Status,
    int MergeRequestCount, bool HasActivePlan);

public record RolloutPlanDto(
    Guid Id, Guid TargetTaskId, string TargetTaskKey, string Version,
    string Source, string Status, bool IsActive, DateTime CreatedAt, DateTime UpdatedAt,
    List<PlanTaskNodeDto> Nodes, List<PlanWaveDto> Waves, List<PlanConflictDto> Conflicts);

public record PlanTaskNodeDto(
    Guid TaskId, string TaskKey, string TaskTitle, bool IsTarget,
    List<Guid> DependsOnTaskIds, List<PlanItemDto> Items);

public record PlanItemDto(
    Guid MergeRequestId, string MrExternalId, string RepositoryName,
    string SourceBranch, string TargetBranch, string MrStatus, int Wave);

public record PlanWaveDto(int Sequence, List<Guid> MergeRequestIds);

// ---- environments and rollouts (mirrors Application.DTOs.RolloutDto + EnvironmentsController) ----

public record EnvironmentDto(Guid Id, string Key, string Name, int Order, bool IsEnabled);

public record RolloutDto(
    Guid Id, Guid TargetTaskId, string TargetTaskKey, Guid EnvironmentId, string EnvironmentKey,
    string Status, DateTime StartedAt, DateTime? FinishedAt, List<RolloutStepDto> Steps);

public record RolloutStepDto(
    Guid Id, Guid MergeRequestId, string MrExternalId, string RepositoryName,
    Guid TaskId, string TaskKey, int Wave, string State, int AttemptCount,
    string? ExternalRef, string? LastError);

public record RolloutSummaryDto(
    Guid Id, Guid TargetTaskId, string TargetTaskKey, string EnvironmentKey,
    string Status, DateTime StartedAt, DateTime? FinishedAt, int StepCount, int SucceededCount);

// ---- action bindings (mirrors ActionBindingsController) ----

public record ActionBindingDto(Guid Id, string EventType, string ActionType, string? Scope, int Order, bool Enabled);

public record ActionTypeDto(string ActionType, List<ActionSettingDto> Settings);

public record ActionSettingDto(string Key, string Label, string? Description, bool Required, bool Secret);
