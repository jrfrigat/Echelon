using Echelon.Core.Enums;

namespace Echelon.Application.DTOs;

// The shapes the admin API answers with. They live here, next to the planning DTOs, because two ends
// have to agree on them: the controllers write them and the admin client reads them. They used to be
// anonymous objects on one side and hand-written records on the other - a contract with nothing
// holding the two halves together, which is exactly how a plan version could become an int on the
// server and stay a string in the browser until the first response failed to deserialize.
//
// A field the API declares as an enum stays an enum here: both ends serialize enums by name.

/// <summary>A repository the service watches, and the connections that reach it.</summary>
/// <param name="Id">The repository id.</param>
/// <param name="Name">Display name.</param>
/// <param name="ExternalId">The provider's own id - for GitLab, the full <c>group/project</c> path.</param>
/// <param name="ConnectionId">The VCS connection that reads it.</param>
/// <param name="ConnectionName">That connection's name, so a list need not resolve it again.</param>
/// <param name="TrackerConnectionId">The tracker whose keys its branches carry, when one is set.</param>
/// <param name="TrackerConnectionName">That tracker's name, when one is set.</param>
public record RepositoryDto(
    Guid Id,
    string Name,
    string ExternalId,
    Guid ConnectionId,
    string ConnectionName,
    Guid? TrackerConnectionId = null,
    string? TrackerConnectionName = null);

/// <summary>A configured VCS connection. Never carries the access token.</summary>
/// <param name="VcsType">
/// The provider type, e.g. <c>gitlab-webhook</c> or <c>gitlab-poll</c> - this is what carries push
/// versus poll. The wire keeps saying "vcsType" while the column is <c>ProviderType</c>: renaming the
/// field would break every client for no gain.
/// </param>
/// <param name="Id">The connection id.</param>
/// <param name="Name">The connection's name, unique across connections.</param>
/// <param name="ApiUrl">Where the provider is reached.</param>
/// <param name="Settings">
/// Provider-specific settings, keyed as the provider declares them. Secret ones are absent, not
/// masked - a mask would be submitted back as though it were the value.
/// </param>
public record VcsConnectionDto(
    Guid Id,
    string Name,
    string VcsType,
    string ApiUrl,
    IReadOnlyDictionary<string, string>? Settings = null);

/// <summary>A configured tracker connection. Never carries the access token.</summary>
/// <param name="Id">The connection id.</param>
/// <param name="Name">The connection's name, unique across connections.</param>
/// <param name="TrackerType">The provider type, e.g. <c>yandextracker-poll</c>.</param>
/// <param name="ApiUrl">Where the tracker is reached.</param>
/// <param name="Settings">Provider-specific settings; secret ones are absent.</param>
public record TrackerConnectionDto(
    Guid Id,
    string Name,
    string TrackerType,
    string ApiUrl,
    IReadOnlyDictionary<string, string>? Settings = null);

/// <summary>A merge request as the list screens show it.</summary>
/// <param name="Id">The local id.</param>
/// <param name="ExternalId">The provider's id, e.g. GitLab's merge request iid.</param>
/// <param name="SourceBranch">The branch being merged.</param>
/// <param name="TargetBranch">The branch merged into.</param>
/// <param name="Status">Where it stands, as the domain reads it.</param>
/// <param name="CreatedAt">When the provider says it was raised.</param>
/// <param name="MergedAt">When it merged, or null.</param>
/// <param name="ClosedAt">When it closed without merging, or null.</param>
/// <param name="IsStatusManual">Whether an operator pinned the status by hand.</param>
/// <param name="RepositoryId">The repository it belongs to.</param>
/// <param name="RepositoryName">That repository's name.</param>
/// <param name="ConnectionName">The connection that reported it.</param>
/// <param name="TaskExternalId">The task its branch names, or null when the rule matched none.</param>
public record MrDto(
    Guid Id,
    string ExternalId,
    string SourceBranch,
    string TargetBranch,
    MergeRequestStatus Status,
    DateTime CreatedAt,
    DateTime? MergedAt,
    DateTime? ClosedAt,
    bool IsStatusManual,
    Guid RepositoryId,
    string RepositoryName,
    string ConnectionName,
    string? TaskExternalId);

/// <summary>One repository-ordering rule: <paramref name="FromRepositoryName"/> deploys after <paramref name="ToRepositoryName"/>.</summary>
/// <param name="Id">The rule id.</param>
/// <param name="FromRepositoryId">The repository that waits.</param>
/// <param name="FromRepositoryName">Its name.</param>
/// <param name="ToRepositoryId">The repository waited on.</param>
/// <param name="ToRepositoryName">Its name.</param>
/// <param name="Type">Whether the rule is hard or advisory.</param>
public record RepositoryOrderingDto(
    Guid Id,
    Guid FromRepositoryId,
    string FromRepositoryName,
    Guid ToRepositoryId,
    string ToRepositoryName,
    StackDependencyType Type);
