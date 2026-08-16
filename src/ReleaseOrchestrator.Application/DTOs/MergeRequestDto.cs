using ReleaseOrchestrator.Core.Enums;

namespace ReleaseOrchestrator.Application.DTOs;

/// <summary>A merge request as the API returns it.</summary>
/// <param name="Id">This service's own identifier.</param>
/// <param name="ExternalId">The provider's identifier, unique within its repository.</param>
/// <param name="SourceBranch">Branch being merged from.</param>
/// <param name="TargetBranch">Branch being merged into.</param>
/// <param name="RepositoryId">The repository it belongs to.</param>
/// <param name="TaskId">
/// The task it was linked to, or null when no task is known yet - routinely the case, since a merge
/// request often names a task that has not been imported.
/// </param>
/// <param name="Status">Where it stands, normalized across providers.</param>
/// <param name="CreatedAt">When the provider opened it. Always UTC.</param>
/// <param name="MergedAt">When it merged, if it did. Always UTC.</param>
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
