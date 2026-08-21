namespace Echelon.Application.DTOs;

/// <summary>What one VCS poll produced.</summary>
/// <param name="Emitted">Merge-request observations raised, each deduplicated as a webhook would be.</param>
/// <param name="Failures">Repositories that could not be read, and why; empty when all were.</param>
/// <param name="Branches">
/// Branches seen. Reported alongside the merge requests because a sweep can be entirely branches: a
/// repository whose work has not reached review yet emits nothing above but still holds a parent task
/// back, and an operator seeing only "emitted: 0" would read that as "nothing happened".
/// </param>
public record VcsPollResultDto(
    int Emitted,
    IReadOnlyList<VcsPollFailureDto> Failures,
    int Branches = 0);
