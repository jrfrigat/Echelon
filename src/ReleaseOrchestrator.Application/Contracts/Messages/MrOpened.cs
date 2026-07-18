namespace ReleaseOrchestrator.Application.Contracts.Messages;

/// <summary>
/// An open merge request's current state. Published for every "opened" webhook, not only
/// the first: GitLab re-sends on label changes, pushes and reopens, and the consumer
/// upserts. <see cref="Labels"/> is what promotes an MR to ReadyForDeploy (README §5).
/// </summary>
public record MrOpened(
    string ConnectionName,
    string RepositoryExternalId,
    string ExternalMrId,
    string SourceBranch,
    string TargetBranch,
    string? TaskExternalId,
    IReadOnlyList<string> Labels,
    // Event identity for dedup. Defaulted so non-ingestion construction (tests) stays terse; the
    // webhook front door sets them, and an empty EventId means "not deduplicated".
    string Source = "",
    string EventId = "") : IMessage, IHasEventIdentity;
