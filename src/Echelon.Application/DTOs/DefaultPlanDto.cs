namespace Echelon.Application.DTOs;

/// <summary>
/// The default rollout plan: the order repositories deploy in before any task's own dependencies
/// or hierarchy are taken into account.
/// </summary>
/// <remarks>
/// Derived from the repository-ordering rules, not stored. It is shown to operators as the answer to
/// "what do these rules actually mean", which a list of pairs does not answer on its own.
/// </remarks>
public record DefaultPlanDto(
    IReadOnlyList<DefaultPlanWaveDto> Waves,
    IReadOnlyList<DefaultPlanConflictDto> Conflicts);
