namespace Echelon.Application.DTOs;

/// <summary>A merge request an operator forced into or out of a task's rollout.</summary>
/// <param name="MergeRequestId">The merge request.</param>
/// <param name="MrExternalId">Its provider id, for display.</param>
/// <param name="RepositoryName">Its repository, for display.</param>
/// <param name="SourceBranch">Its branch, for display.</param>
/// <param name="MrStatus">Its current status - an excluded merge request may since have been merged.</param>
/// <param name="State">"Included" or "Excluded".</param>
public record PlanMembershipDto(
    Guid MergeRequestId,
    string MrExternalId,
    string RepositoryName,
    string SourceBranch,
    string MrStatus,
    string State);
