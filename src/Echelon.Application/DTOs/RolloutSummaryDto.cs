namespace Echelon.Application.DTOs;

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
