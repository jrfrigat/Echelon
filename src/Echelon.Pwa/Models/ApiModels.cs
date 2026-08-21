using Echelon.Application.DTOs;
using Echelon.Core.Enums;
using Echelon.Providers.Abstractions;

namespace Echelon.Pwa.Models;

// What is left here is the shape of a response the API builds itself - a controller projection with no
// type behind it. Everything the server already declares as a DTO is USED from
// <see cref="Echelon.Application.DTOs"/> rather than copied: a copy is a promise nobody checks, and the
// one that drifted (a plan version that became an int on the server and stayed a string here) took
// down every plan the UI tried to read.
//
// A field the API declares as an enum is an enum here too, which works because both ends read and write
// the enum's name - the API registers JsonStringEnumConverter, and so does ApiService. A closed set the
// server owns is never re-spelled here as a string literal: the compiler cannot check "Poll", and a
// rename would leave the UI quietly comparing against nothing.

/// <summary>
/// One field a provider declares for its connections, described well enough to render it.
/// </summary>
/// <remarks>
/// This is why the connection dialogs contain no provider's vocabulary. The form is built from
/// whatever the server says the selected provider needs, so a new provider - or a new setting on an
/// existing one - reaches the UI without a change here.
/// </remarks>
/// <param name="Secret">Render write-only. The API never sends a stored secret back.</param>
/// <param name="Kind">Which control to render and how to validate.</param>
/// <param name="Options">Allowed values, for an <see cref="ProviderSettingKind.Enum"/> field. Null otherwise.</param>
/// <param name="Default">Value to pre-fill for a new connection. Null when there is none.</param>
/// <param name="Min">Inclusive lower bound for an <see cref="ProviderSettingKind.Int"/> field; null for no bound.</param>
/// <param name="Max">Inclusive upper bound for an <see cref="ProviderSettingKind.Int"/> field; null for no bound.</param>
public record ProviderSettingDto(
    string Key, string Label, string? Description, bool Required, bool Secret,
    ProviderSettingKind Kind = ProviderSettingKind.Text, List<string>? Options = null, string? Default = null,
    int? Min = null, int? Max = null);

/// <summary>A provider that can back a connection, the settings it declares, and how its events arrive.</summary>
/// <param name="Ingestion">Push or Poll for a VCS or tracker provider; null for a deploy strategy.</param>
public record ProviderTypeDto(string ProviderType, List<ProviderSettingDto> Settings, IngestionMode? Ingestion = null);

/// <summary>What a manual VCS poll produced: observations emitted, and repositories that could not be read.</summary>
public record PollResultDto(int Emitted, List<PollFailureDto>? Failures = null, int Branches = 0);

/// <summary>A repository the poll could not read, and why (usually a wrong external id or token access).</summary>
public record PollFailureDto(string Repository, string Reason);

/// <summary>What a manual tracker poll produced.</summary>
/// <param name="Emitted">Syncs requested, over the issues the tracker reported open and the tasks already known.</param>
/// <param name="Discovered">How many of those the tracker turned up that were not in the database yet.</param>
/// <param name="Failure">Why the tracker could not be searched; null when it was. The known tasks were still re-read.</param>
public record TrackerPollResultDto(int Emitted, int Discovered, string? Failure = null);

/// <summary>An installed plugin (a connector, deploy strategy or action handler) for the admin overview.</summary>
/// <param name="Category">Which axis the plugin extends.</param>
/// <param name="Ingestion">Push or Poll for a connector; null for a deploy strategy or action handler.</param>
public record PluginDto(PluginCategory Category, string Key, IngestionMode? Ingestion, string? Description);

/// <param name="VcsType">The provider type, e.g. <c>gitlab-webhook</c> or <c>gitlab-poll</c> - this is what carries push vs poll.</param>
/// <param name="Settings">
/// Provider-specific settings, keyed as the provider declares them. Secret ones are absent, not
/// masked - a mask would be submitted back as though it were the value. See
/// <c>ProviderSettingsFields</c>.
/// </param>
public record VcsConnectionDto(
    Guid Id, string Name, string VcsType, string ApiUrl,
    string? ConnectionName = null, Dictionary<string, string>? Settings = null);

/// <param name="Settings">Provider-specific settings; secret ones are absent.</param>
public record TrackerConnectionDto(
    Guid Id, string Name, string TrackerType, string ApiUrl,
    Dictionary<string, string>? Settings = null);

public record RepositoryDto(Guid Id, string Name, string ExternalId, Guid ConnectionId, string ConnectionName);

// ---- default rollout plan (repository ordering; mirrors RepositoryOrderingController) ----

/// <summary>One ordering rule: <see cref="FromRepositoryName"/> deploys after <see cref="ToRepositoryName"/>.</summary>
public record RepositoryOrderingDto(
    Guid Id, Guid FromRepositoryId, string FromRepositoryName,
    Guid ToRepositoryId, string ToRepositoryName, string Type);


// ---- request audit (mirrors RequestAuditController) ----

public record MrDto(
    Guid Id, string ExternalId, string SourceBranch, string TargetBranch,
    string Status, DateTime CreatedAt, Guid RepositoryId,
    string RepositoryName, string ConnectionName, string? TaskExternalId,
    bool IsStatusManual = false);

public record PagedResult<T>(int Total, int Page, int PageSize, List<T> Items);

// ---- ordering rules (mirrors PlanningController) ----

/// <summary>The ordering-rule document, as text.</summary>
public record OrderingRulesDocumentDto(string Document);

/// <summary>What checking a document found.</summary>
/// <param name="IsValid">Whether it would be accepted.</param>
/// <param name="Problems">Everything wrong with it; empty when valid.</param>
/// <param name="Groups">
/// What each group selects right now. The useful half: a document can be perfectly valid and select
/// nothing, since a glob with a typo is still a well-formed glob.
/// </param>
public record OrderingRulesValidationDto(
    bool IsValid, List<string> Problems, List<OrderingRuleGroupMatchDto> Groups);

/// <summary>What one group currently selects.</summary>
public record OrderingRuleGroupMatchDto(string Group, int Matched, List<string> Examples);

// ---- deployable work (mirrors WorkItemsController) ----

/// <summary>
/// One piece of deployable work: a task's presence in a repository, and what carries it.
/// </summary>
/// <remarks>
/// The row is (task, repository), not the merge request. A connector reports that a task has work
/// somewhere; before a merge request is raised the work is a branch, and the task is the same either
/// way. <c>State</c> is <c>New</c> for a branch nothing has raised yet.
/// </remarks>
public record WorkItemDto(
    WorkItemKind Kind,
    string? TaskKey,
    string RepositoryName,
    string ConnectionName,
    string Carrier,
    string Branch,
    string State,
    bool IsStatusManual,
    List<string> Labels,
    string? PipelineResult,
    WorkItemReadinessDto? Readiness,
    DateTime At);

/// <summary>How one piece of work stands against one environment's readiness rule.</summary>
public record WorkItemReadinessDto(string Status, bool IsReady, List<string> MissingSignals);

/// <summary>A page of work items, with a flag for when the scan cap bound.</summary>
public record WorkItemsResult(int Total, int Page, int PageSize, List<WorkItemDto> Items, bool Truncated);

public record PermissionClaimDto(Guid Id, string Name);
public record GroupMappingDto(Guid Id, string AdGroupSid, string ClaimName);

// ---- per-task rollout plans (mirrors Application.DTOs.RolloutPlanDto) ----

// ---- task timeline (mirrors Application.DTOs.TaskTimelineDto) ----

/// <summary>A merge request an operator forced into or out of a task's rollout.</summary>
public record PlanMembershipDto(
    Guid MergeRequestId, string MrExternalId, string RepositoryName,
    string SourceBranch, string MrStatus, string State);

/// <summary>A named selector in the ordering rules, as the visual editor holds it.</summary>
/// <remarks>Mutable, unlike the read DTOs: this one is bound to form fields.</remarks>
public class OrderingRuleGroupDto
{
    public string Name { get; set; } = "";
    public List<string> Connectors { get; set; } = [];
    public List<string> Repositories { get; set; } = [];
    public List<string> Branches { get; set; } = [];
    public List<string> TaskKeys { get; set; } = [];
    public List<string> Labels { get; set; } = [];
}

/// <summary>One ordering rule, as the visual editor holds it.</summary>
public class OrderingRuleOrderDto
{
    public string Group { get; set; } = "";
    public List<string> Needs { get; set; } = [];
    public string Type { get; set; } = "Hard";
    public string Scope { get; set; } = "AcrossPlan";
}

/// <summary>The ordering rules as a structure.</summary>
/// <param name="Editable">
/// False when the stored document says something the form cannot express, so the form must not own it.
/// </param>
public record OrderingRulesModelDto(
    bool Editable,
    List<string> Problems,
    List<OrderingRuleGroupDto> Groups,
    List<OrderingRuleOrderDto> Order);

// ---- environments and rollouts (mirrors Application.DTOs.RolloutDto + EnvironmentsController) ----

/// <param name="ReadinessRuleId">The environment's default readiness rule, or null for no gate.</param>
/// <param name="ReadinessRuleName">The rule's name, for display; null when ungated.</param>
public record EnvironmentDto(
    Guid Id, string Key, string Name, int Order, bool IsEnabled,
    Guid? ReadinessRuleId = null, string? ReadinessRuleName = null);

/// <summary>A named readiness rule (mirrors ReadinessRulesController).</summary>
/// <param name="Mode">"AnyOf" or "AllOf".</param>
/// <param name="RequiredSignals">The signal tokens a merge request must carry, e.g. label:ready-for-prod.</param>
public record ReadinessRuleDto(Guid Id, string Name, string Mode, List<string> RequiredSignals);

/// <summary>A repository's deploy configuration for one environment (mirrors DeployTargetsController).</summary>
/// <param name="Settings">Strategy settings, keyed as the strategy declares them; secret ones absent.</param>
public record DeployTargetDto(
    Guid Id, Guid RepositoryId, string RepositoryName, Guid EnvironmentId, string EnvironmentKey,
    string DeployStrategyKey, string RedeployPolicy, Dictionary<string, string>? Settings = null,
    Guid? ReadinessRuleId = null, string? ReadinessRuleName = null);

/// <summary>A person's readiness override for one merge request in one environment (mirrors ReadinessPinsController).</summary>
public record ReadinessPinDto(
    Guid Id, Guid MergeRequestId, Guid EnvironmentId, string EnvironmentKey,
    bool IsReady, string? Reason, string? ActorName, DateTime At);

// ---- action bindings (mirrors ActionBindingsController) ----

public record ActionBindingDto(Guid Id, string EventType, string ActionType, string? Scope, int Order, bool Enabled);

/// <summary>An action handler and the settings it declares, in the same shape a provider uses.</summary>
/// <remarks>
/// Shares <see cref="ProviderSettingDto"/> with connections because the server declares both through
/// one <c>ProviderSettingSchema</c>; a separate mirror here would be a second thing to keep in step
/// with it for no difference in content.
/// </remarks>
public record ActionTypeDto(string ActionType, List<ProviderSettingDto> Settings);
