namespace Echelon.Application.DTOs;

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
