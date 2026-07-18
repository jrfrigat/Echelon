namespace ReleaseOrchestrator.Application.DTOs;

/// <summary>A rollout run and its steps -- what the progress view shows.</summary>
public record RolloutDto(
    Guid Id,
    Guid TargetTaskId,
    string TargetTaskKey,
    Guid EnvironmentId,
    string EnvironmentKey,
    string Status,
    DateTime StartedAt,
    DateTime? FinishedAt,
    IReadOnlyList<RolloutStepDto> Steps);

/// <summary>One step of a rollout: a merge request's deploy.</summary>
public record RolloutStepDto(
    Guid Id,
    Guid MergeRequestId,
    string MrExternalId,
    string RepositoryName,
    Guid TaskId,
    string TaskKey,
    int Wave,
    string State,
    int AttemptCount,
    string? ExternalRef,
    string? LastError);

/// <summary>A rollout as it appears in a history list.</summary>
public record RolloutSummaryDto(
    Guid Id,
    Guid TargetTaskId,
    string TargetTaskKey,
    string EnvironmentKey,
    string Status,
    DateTime StartedAt,
    DateTime? FinishedAt,
    int StepCount,
    int SucceededCount);
