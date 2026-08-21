namespace Echelon.Application.DTOs;

/// <summary>What checking a document found.</summary>
/// <param name="IsValid">Whether it would be accepted.</param>
/// <param name="Problems">Everything wrong with it; empty when valid.</param>
/// <param name="Groups">What each group selects right now - a valid rule can still match nothing.</param>
public record OrderingRulesValidationDto(
    bool IsValid, IReadOnlyList<string> Problems, IReadOnlyList<OrderingRuleGroupMatchDto> Groups);
