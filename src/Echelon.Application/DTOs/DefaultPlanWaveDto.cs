namespace Echelon.Application.DTOs;

/// <summary>One wave of the default plan: repositories that deploy in parallel.</summary>
public record DefaultPlanWaveDto(int Sequence, IReadOnlyList<DefaultPlanRepositoryDto> Repositories);
