namespace Echelon.Application.DTOs;

/// <summary>An environment a rollout can deploy into.</summary>
/// <param name="Id">The environment id.</param>
/// <param name="Key">Its key, e.g. <c>staging</c>. Unique, and what rules and targets refer to.</param>
/// <param name="Name">Its display name.</param>
/// <param name="Order">Where it sits in the promotion chain; lower deploys first.</param>
/// <param name="IsEnabled">Whether anything may deploy into it at all.</param>
/// <param name="ReadinessRuleId">Its default readiness rule, or null for no gate.</param>
/// <param name="ReadinessRuleName">That rule's name, so a list need not resolve it again.</param>
public record EnvironmentDto(
    Guid Id,
    string Key,
    string Name,
    int Order,
    bool IsEnabled,
    Guid? ReadinessRuleId = null,
    string? ReadinessRuleName = null);
