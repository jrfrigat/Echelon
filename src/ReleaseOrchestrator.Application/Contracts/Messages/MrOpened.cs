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
    // The candidates the task-key rule reads from — branch, title, labels — rather than a key the
    // parser resolved. Linking is now a per-connection rule the consumer applies (which needs the
    // connection's settings the parser cannot see), so the raw fields travel and the consumer extracts.
    string? Title,
    IReadOnlyList<string> Labels,
    // Event identity for dedup. Defaulted so non-ingestion construction (tests) stays terse; the
    // webhook front door sets them, and an empty EventId means "not deduplicated".
    string Source = "",
    string EventId = "") : IMessage, IHasEventIdentity;
