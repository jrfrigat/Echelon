namespace Echelon.Application.DTOs;

/// <summary>One execution wave: merge requests that deploy in parallel.</summary>
public record PlanWaveDto(int Sequence, IReadOnlyList<Guid> MergeRequestIds);
