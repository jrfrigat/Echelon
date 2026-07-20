namespace ReleaseOrchestrator.Pwa.Models;

// Mirrors ReleaseOrchestrator.Application.DTOs. Enum-valued fields are strings here, which
// only works because the API registers JsonStringEnumConverter — keep the two in step.

/// <summary>A dependency the plan could not honour; shown to operators as a warning. Shared by the per-task rollout plan.</summary>
public record PlanConflictDto(
    string Kind, Guid FromMergeRequestId, Guid ToMergeRequestId, string Reason);

/// <param name="IngestionMode">"Push" (webhooks, the default) or "Poll" for a VCS that cannot reach us.</param>
public record VcsConnectionDto(
    Guid Id, string Name, string VcsType, string ApiUrl,
    string? ReadyForDeployLabel = null, string? ConnectionName = null,
    string? IngestionMode = null);

public record TrackerConnectionDto(Guid Id, string Name, string TrackerType, string ApiUrl, string? OrgId);

public record RepositoryDto(Guid Id, string Name, string ExternalId, Guid ConnectionId, string ConnectionName);

// ---- default rollout plan (repository ordering; mirrors RepositoryOrderingController) ----

/// <summary>One ordering rule: <see cref="FromRepositoryName"/> deploys after <see cref="ToRepositoryName"/>.</summary>
public record RepositoryOrderingDto(
    Guid Id, Guid FromRepositoryId, string FromRepositoryName,
    Guid ToRepositoryId, string ToRepositoryName, string Type);

public record DefaultPlanRepositoryDto(Guid Id, string Name);
public record DefaultPlanWaveDto(int Sequence, List<DefaultPlanRepositoryDto> Repositories);
public record DefaultPlanConflictDto(string FromRepositoryName, string ToRepositoryName, string Kind, string Reason);

/// <summary>The order the ordering rules add up to, plus any rule that could not be honoured.</summary>
public record DefaultPlanDto(List<DefaultPlanWaveDto> Waves, List<DefaultPlanConflictDto> Conflicts);

// ---- request audit (mirrors RequestAuditController) ----

public record RequestAuditEntryDto(
    Guid Id, string Method, string RoutePattern, string Path, string Kind,
    int StatusCode, int DurationMs, DateTime StartedAt,
    string? UserId, string? UserName, string? PeerIp, string? ForwardedIp,
    string? Permission, string CorrelationId, string? ExceptionType, string Instance);

public record RequestAuditRouteStatDto(
    string Method, string RoutePattern, long Count,
    long ClientErrors, long ServerErrors, int P50Ms, int P95Ms, int MaxMs);

/// <summary>The window at a glance. The four trailing flags are what the page cannot vouch for.</summary>
public record RequestAuditSummaryDto(
    DateTime FromUtc, DateTime ToUtc, long Total, long Errors,
    List<RequestAuditRouteStatDto> Routes,
    bool PercentilesApproximate, long DroppedRecords, bool AnonymousCapBound, bool IngressCovered);

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

public record TaskRefDto(Guid Id, string ExternalId, string Title);

// ---- task timeline (mirrors Application.DTOs.TaskTimelineDto) ----

public record TaskTimelineDto(
    Guid TaskId, string TaskExternalId, bool IsArchived,
    DateTime? FirstSeenAt, string? FirstSeenSource,
    TimelineCoverageDto Coverage, List<TimelineEntryDto> Entries);

/// <summary>What the timeline cannot say. Rendered, never swallowed: an unexplained gap reads as "nothing happened".</summary>
public record TimelineCoverageDto(DateTime? RecordingBeganAt, bool Truncated, bool AttributionIsShared);

public record TimelineEntryDto(
    DateTime At, string Category, string Kind,
    string? ActorOid, string? ActorKind, string? ActorName,
    string? SubjectKey, string? Detail, string ClockSource,
    int Repetitions, DateTime? RepeatedUntil,
    Guid? RolloutId, Guid? MergeRequestId, bool IsReassigned);

/// <summary>A task's own facts and its place in the hierarchy — readable before any plan exists.</summary>
public record TaskDetailDto(
    Guid Id, string ExternalId, string Title, string Status,
    TaskRefDto? Parent, List<TaskRefDto> Children);

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
