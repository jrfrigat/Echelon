using Echelon.Core.Enums;

namespace Echelon.Application.DTOs;
/// <summary>A merge request as the list screens show it.</summary>
/// <param name="Id">The local id.</param>
/// <param name="ExternalId">The provider's id, e.g. GitLab's merge request iid.</param>
/// <param name="SourceBranch">The branch being merged.</param>
/// <param name="TargetBranch">The branch merged into.</param>
/// <param name="Status">Where it stands, as the domain reads it.</param>
/// <param name="CreatedAt">When the provider says it was raised.</param>
/// <param name="MergedAt">When it merged, or null.</param>
/// <param name="ClosedAt">When it closed without merging, or null.</param>
/// <param name="IsStatusManual">Whether an operator pinned the status by hand.</param>
/// <param name="RepositoryId">The repository it belongs to.</param>
/// <param name="RepositoryName">That repository's name.</param>
/// <param name="ConnectionName">The connection that reported it.</param>
/// <param name="TaskExternalId">The task its branch names, or null when the rule matched none.</param>
public record MrDto(
    Guid Id,
    string ExternalId,
    string SourceBranch,
    string TargetBranch,
    MergeRequestStatus Status,
    DateTime CreatedAt,
    DateTime? MergedAt,
    DateTime? ClosedAt,
    bool IsStatusManual,
    Guid RepositoryId,
    string RepositoryName,
    string ConnectionName,
    string? TaskExternalId);
