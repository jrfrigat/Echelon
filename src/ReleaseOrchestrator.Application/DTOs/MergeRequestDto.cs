using ReleaseOrchestrator.Core.Enums;

namespace ReleaseOrchestrator.Application.DTOs;

public record MergeRequestDto(
    Guid Id,
    string ExternalId,
    string SourceBranch,
    string TargetBranch,
    Guid RepositoryId,
    Guid? TaskId,
    MergeRequestStatus Status,
    DateTime CreatedAt,
    DateTime? MergedAt);
