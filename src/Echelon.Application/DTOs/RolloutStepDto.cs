namespace Echelon.Application.DTOs;

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
