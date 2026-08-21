using Echelon.Core.Enums;

namespace Echelon.Application.DTOs;

// The configuration screens' shapes: environments, readiness, deploy targets, action bindings and
// permissions. Same reasoning as the rest of this folder - the controllers write them and the admin
// client reads them, so they are declared once where both can see them.

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

/// <summary>A named readiness rule: the signals a merge request must carry to pass a gate.</summary>
/// <param name="Id">The rule id.</param>
/// <param name="Name">Its name, as an operator chose it.</param>
/// <param name="Mode">Whether every signal is required, or any one of them.</param>
/// <param name="RequiredSignals">The signal tokens, e.g. <c>label:ready-for-prod</c>, <c>mr-status:merged</c>.</param>
public record ReadinessRuleDto(
    Guid Id,
    string Name,
    ReadyRule Mode,
    IReadOnlyList<string> RequiredSignals);

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

/// <summary>An action bound to an event: what to do, and when.</summary>
/// <param name="Id">The binding id.</param>
/// <param name="EventType">The event that triggers it.</param>
/// <param name="ActionType">The handler that runs.</param>
/// <param name="Scope">What it is limited to, or null for everything.</param>
/// <param name="Order">Where it runs among the bindings for the same event; lower first.</param>
/// <param name="Enabled">Whether it runs at all.</param>
public record ActionBindingDto(
    Guid Id,
    string EventType,
    string ActionType,
    string? Scope,
    int Order,
    bool Enabled);

/// <summary>A permission claim a group or a user can hold.</summary>
/// <param name="Id">The claim id.</param>
/// <param name="Name">Its name, as the authorization policies name it.</param>
public record PermissionClaimDto(Guid Id, string Name);

/// <summary>A directory group mapped to a claim, so its members inherit the permission.</summary>
/// <param name="Id">The mapping id.</param>
/// <param name="AdGroupSid">The group's security identifier.</param>
/// <param name="ClaimName">The claim its members are granted.</param>
public record GroupMappingDto(Guid Id, string AdGroupSid, string ClaimName);
