namespace Echelon.Application.DTOs;

/// <summary>A person's readiness override for one merge request in one environment.</summary>
/// <param name="Id">The pin id.</param>
/// <param name="MergeRequestId">The merge request pinned.</param>
/// <param name="EnvironmentId">The environment it is pinned for.</param>
/// <param name="EnvironmentKey">That environment's key.</param>
/// <param name="IsReady">Which way it was pinned: through, or held.</param>
/// <param name="Reason">Why, when whoever pinned it said.</param>
/// <param name="ActorName">Who pinned it, as far as the audit could tell.</param>
/// <param name="At">When.</param>
public record ReadinessPinDto(
    Guid Id,
    Guid MergeRequestId,
    Guid EnvironmentId,
    string EnvironmentKey,
    bool IsReady,
    string? Reason,
    string? ActorName,
    DateTime At);
