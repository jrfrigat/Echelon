using Echelon.Core.Enums;
using Echelon.Providers.Abstractions;

namespace Echelon.Application.DTOs;

/// <summary>
/// A provider a connection can be backed by: what it is called, what it needs configured, and how its
/// events arrive.
/// </summary>
/// <remarks>
/// The settings are the adapter's own <see cref="ProviderSettingSchema"/>, passed through rather than
/// re-described. The admin form is built from whatever the selected provider declares, so a new
/// provider - or a new setting on an existing one - reaches the UI with no change on either side.
/// </remarks>
/// <param name="ProviderType">The canonical key a connection stores, e.g. <c>gitlab-poll</c>.</param>
/// <param name="Settings">The fields this provider declares, in the order it declares them.</param>
/// <param name="Ingestion">Push or Poll for a connector; null for a deploy strategy, which has neither.</param>
public record ProviderTypeDto(
    string ProviderType,
    IReadOnlyList<ProviderSettingSchema> Settings,
    IngestionMode? Ingestion = null);

/// <summary>An installed plugin, for the admin overview of what this build can talk to.</summary>
/// <remarks>
/// Echoed from the marker registrations the composition root added: this is each plugin's own
/// declaration, not a classification made here.
/// </remarks>
/// <param name="Category">Which axis the plugin extends.</param>
/// <param name="Key">Its registered key - provider type, strategy key or action type.</param>
/// <param name="Ingestion">Push or Poll for a connector; null for a deploy strategy or action handler.</param>
/// <param name="Description">The plugin's own one-line description; null when it declared none.</param>
public record PluginDto(
    PluginCategory Category,
    string Key,
    IngestionMode? Ingestion,
    string? Description);

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

/// <summary>A repository a poll could not read, and why.</summary>
/// <param name="Repository">The repository, as configured - usually where the mistake is.</param>
/// <param name="Reason">A human explanation, aimed at that misconfiguration.</param>
public record VcsPollFailureDto(string Repository, string Reason);

/// <summary>What one tracker poll produced.</summary>
/// <param name="Emitted">Syncs requested, over the issues the tracker reported open and the tasks already known.</param>
/// <param name="Discovered">How many of those the tracker turned up that were not in the database yet.</param>
/// <param name="Failure">Why the tracker could not be searched; null when it was. The known tasks were still re-read.</param>
public record TrackerPollResultDto(int Emitted, int Discovered, string? Failure = null);

/// <summary>An action handler and the settings it declares.</summary>
/// <remarks>
/// The schema is the same <see cref="ProviderSettingSchema"/> a provider uses, whole: the endpoint
/// used to send five of its ten fields, which the client read into a full one and filled the rest with
/// defaults - so a bounded number rendered as a text box and nobody could see why.
/// </remarks>
/// <param name="ActionType">The handler's registered key.</param>
/// <param name="Settings">The fields it declares, in the order it declares them.</param>
public record ActionTypeDto(string ActionType, IReadOnlyList<ProviderSettingSchema> Settings);
