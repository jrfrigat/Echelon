namespace Echelon.Application.DTOs;

/// <summary>What one group currently selects.</summary>
/// <param name="Group">The group's name.</param>
/// <param name="Matched">How many merge requests it selects.</param>
/// <param name="Examples">A few of them, as <c>repository:branch</c>, to confirm it selected what was meant.</param>
public record OrderingRuleGroupMatchDto(string Group, int Matched, IReadOnlyList<string> Examples);
