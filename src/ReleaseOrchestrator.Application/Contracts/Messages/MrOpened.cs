namespace ReleaseOrchestrator.Application.Contracts.Messages;

/// <summary>
/// An open merge request's current state. Published for every "opened" webhook, not only
/// the first: GitLab re-sends on label changes, pushes and reopens, and the consumer
/// upserts.
/// </summary>
/// <remarks>
/// <see cref="Labels"/> carries the full current set, not a promotion. A label no longer moves a
/// merge request to a "ready" status — that was retired with the ready-for-deploy label; the set is
/// stored so a per-environment readiness rule can be evaluated against it at launch.
/// </remarks>
/// <param name="ConnectionName">The VCS connection the observation came through.</param>
/// <param name="RepositoryExternalId">The repository, as the provider identifies it.</param>
/// <param name="ExternalMrId">The provider's own id for the merge request.</param>
/// <param name="SourceBranch">Branch being merged from.</param>
/// <param name="TargetBranch">Branch being merged into.</param>
/// <param name="Title">The merge request title.</param>
/// <param name="Labels">Every label currently on the merge request.</param>
/// <param name="PipelineResult">The latest pipeline result, or null when the source cannot report one.</param>
/// <param name="Source">Which ingestion path produced this, used to scope the event id.</param>
/// <param name="EventId">Deduplication identity; empty means "not deduplicated".</param>
public record MrOpened(
    string ConnectionName,
    string RepositoryExternalId,
    string ExternalMrId,
    string SourceBranch,
    string TargetBranch,
    // The candidates the task-key rule reads from — branch, title, labels — rather than a key the
    // parser resolved. Linking is now a per-connection rule the consumer applies (which needs the
    // connection's settings the parser cannot see), so the raw fields travel and the consumer extracts.
    string? Title,
    IReadOnlyList<string> Labels,
    // The latest pipeline result, when the source knows it (the poll path reads it from the API); null
    // from the webhook path, which does not carry pipeline status. A readiness rule can require it.
    string? PipelineResult = null,
    // Event identity for dedup. Defaulted so non-ingestion construction (tests) stays terse; the
    // webhook front door sets them, and an empty EventId means "not deduplicated".
    string Source = "",
    string EventId = "") : IMessage, IHasEventIdentity;
