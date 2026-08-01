namespace ReleaseOrchestrator.Pwa.Models;

// Mirrors ReleaseOrchestrator.Application.DTOs. Enum-valued fields are strings here, which
// only works because the API registers JsonStringEnumConverter — keep the two in step.

/// <summary>A dependency the plan could not honour; shown to operators as a warning. Shared by the per-task rollout plan.</summary>
public record PlanConflictDto(
    string Kind, Guid FromMergeRequestId, Guid ToMergeRequestId, string Reason);

/// <summary>
/// One field a provider declares for its connections, described well enough to render it.
/// </summary>
/// <remarks>
/// This is why the connection dialogs contain no provider's vocabulary. The form is built from
/// whatever the server says the selected provider needs, so a new provider — or a new setting on an
/// existing one — reaches the UI without a change here.
/// </remarks>
/// <param name="Secret">Render write-only. The API never sends a stored secret back.</param>
/// <param name="Kind">"Text" (default), "Int", "Enum" or "Regex" — which control to render and how to validate.</param>
/// <param name="Options">Allowed values, for an "Enum" field. Null otherwise.</param>
/// <param name="Default">Value to pre-fill for a new connection. Null when there is none.</param>
/// <param name="Min">Inclusive lower bound for an "Int" field; null for no bound.</param>
/// <param name="Max">Inclusive upper bound for an "Int" field; null for no bound.</param>
public record ProviderSettingDto(
    string Key, string Label, string? Description, bool Required, bool Secret,
    string Kind = "Text", List<string>? Options = null, string? Default = null,
    int? Min = null, int? Max = null);

/// <summary>A provider that can back a connection, the settings it declares, and (for VCS) push vs poll.</summary>
/// <param name="Ingestion">"Push" or "Poll" for a VCS provider; null for trackers and deploy strategies.</param>
public record ProviderTypeDto(string ProviderType, List<ProviderSettingDto> Settings, string? Ingestion = null);

/// <summary>What a manual poll produced: observations emitted, and repositories that could not be read.</summary>
public record PollResultDto(int Emitted, List<PollFailureDto>? Failures = null);

/// <summary>A repository the poll could not read, and why (usually a wrong external id or token access).</summary>
public record PollFailureDto(string Repository, string Reason);

/// <summary>An installed plugin (a connector, deploy strategy or action handler) for the admin overview.</summary>
/// <param name="Category">Which kind: <c>vcs</c>, <c>tracker</c>, <c>deploy</c> or <c>action</c>.</param>
/// <param name="Ingestion">"Push" or "Poll" for a VCS connector; null otherwise.</param>
public record PluginDto(string Category, string Key, string? Ingestion, string? Description);

/// <param name="VcsType">The provider type, e.g. <c>gitlab-webhook</c> or <c>gitlab-poll</c> — this is what carries push vs poll.</param>
/// <param name="Settings">
/// Provider-specific settings, keyed as the provider declares them. Secret ones are absent, not
/// masked — a mask would be submitted back as though it were the value. See
/// <c>ProviderSettingsFields</c>.
/// </param>
public record VcsConnectionDto(
    Guid Id, string Name, string VcsType, string ApiUrl,
    string? ConnectionName = null, Dictionary<string, string>? Settings = null);

/// <param name="Settings">Provider-specific settings; secret ones are absent.</param>
public record TrackerConnectionDto(
    Guid Id, string Name, string TrackerType, string ApiUrl,
    Dictionary<string, string>? Settings = null);

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

/// <param name="ReadinessRuleId">The environment's default readiness rule, or null for no gate.</param>
/// <param name="ReadinessRuleName">The rule's name, for display; null when ungated.</param>
public record EnvironmentDto(
    Guid Id, string Key, string Name, int Order, bool IsEnabled,
    Guid? ReadinessRuleId = null, string? ReadinessRuleName = null);

/// <summary>A named readiness rule (mirrors ReadinessRulesController).</summary>
/// <param name="Mode">"AnyOf" or "AllOf".</param>
/// <param name="RequiredSignals">The signal tokens a merge request must carry, e.g. label:ready-for-prod.</param>
public record ReadinessRuleDto(Guid Id, string Name, string Mode, List<string> RequiredSignals);

/// <summary>A repository's deploy configuration for one environment (mirrors DeployTargetsController).</summary>
/// <param name="Settings">Strategy settings, keyed as the strategy declares them; secret ones absent.</param>
public record DeployTargetDto(
    Guid Id, Guid RepositoryId, string RepositoryName, Guid EnvironmentId, string EnvironmentKey,
    string DeployStrategyKey, string RedeployPolicy, Dictionary<string, string>? Settings = null,
    Guid? ReadinessRuleId = null, string? ReadinessRuleName = null);

/// <summary>A person's readiness override for one merge request in one environment (mirrors ReadinessPinsController).</summary>
public record ReadinessPinDto(
    Guid Id, Guid MergeRequestId, Guid EnvironmentId, string EnvironmentKey,
    bool IsReady, string? Reason, string? ActorName, DateTime At);

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

/// <summary>An action handler and the settings it declares, in the same shape a provider uses.</summary>
/// <remarks>
/// Shares <see cref="ProviderSettingDto"/> with connections because the server declares both through
/// one <c>ProviderSettingSchema</c>; a separate mirror here would be a second thing to keep in step
/// with it for no difference in content.
/// </remarks>
public record ActionTypeDto(string ActionType, List<ProviderSettingDto> Settings);
