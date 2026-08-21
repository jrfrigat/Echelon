using Echelon.Core.Enums;

namespace Echelon.Application.DTOs;
/// <summary>A repository's deploy configuration for one environment.</summary>
/// <param name="Id">The target id.</param>
/// <param name="RepositoryId">The repository deployed.</param>
/// <param name="RepositoryName">Its name.</param>
/// <param name="EnvironmentId">The environment deployed into.</param>
/// <param name="EnvironmentKey">Its key.</param>
/// <param name="DeployStrategyKey">Which strategy ships it - merge, pipeline trigger, or another.</param>
/// <param name="RedeployPolicy">What a second deploy of the same merge request does.</param>
/// <param name="Settings">Strategy settings, keyed as the strategy declares them; secret ones absent.</param>
/// <param name="ReadinessRuleId">A rule overriding the environment's default, or null to use it.</param>
/// <param name="ReadinessRuleName">That override's name, when there is one.</param>
public record DeployTargetDto(
    Guid Id,
    Guid RepositoryId,
    string RepositoryName,
    Guid EnvironmentId,
    string EnvironmentKey,
    string DeployStrategyKey,
    RedeployPolicy RedeployPolicy,
    IReadOnlyDictionary<string, string>? Settings = null,
    Guid? ReadinessRuleId = null,
    string? ReadinessRuleName = null);
