namespace Echelon.Application.DTOs;

/// <summary>A merge request in the plan, with the wave it deploys in.</summary>
/// <param name="ManuallyIncluded">
/// True when an operator forced this merge request in rather than the derivation choosing it - worth
/// showing, because it is the one row in the plan that does not follow from the atlas.
/// </param>
public record PlanItemDto(
    Guid MergeRequestId,
    string MrExternalId,
    string RepositoryName,
    string SourceBranch,
    string TargetBranch,
    string MrStatus,
    int Wave,
    bool ManuallyIncluded);
