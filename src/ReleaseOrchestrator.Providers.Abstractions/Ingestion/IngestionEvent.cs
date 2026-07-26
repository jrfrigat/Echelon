using ReleaseOrchestrator.Core.Enums;

namespace ReleaseOrchestrator.Providers.Abstractions.Ingestion;

/// <summary>
/// A normalized observation a webhook (or a poll) produced, before it becomes a bus message.
/// </summary>
/// <remarks>
/// <para>
/// This family is deliberately a <b>parallel</b> to the Rebus messages in
/// <c>Application.Contracts.Messages</c>, not the same types. A provider must not reference
/// <c>Application</c> — that assembly already references this one, so the reverse is a build cycle —
/// and it must not reference Rebus. So the parser returns these, and the host maps them to the
/// wire messages one-for-one.
/// </para>
/// <para>
/// Keeping the wire messages where they are is not only about the cycle: Rebus routes and
/// deserializes by assembly-qualified type name, so moving <c>MrOpened</c> to a different assembly
/// would change its wire identity and strand in-flight messages across a rolling deploy. The one
/// extra record per event shape is the price of not touching the wire contract.
/// </para>
/// <para>
/// <c>Source</c> and <c>EventId</c> are NOT on these records. They are identity the host stamps —
/// the host knows the provider key and the connection name and owns the one canonical spelling of
/// <c>"{providerType}/{connectionName}"</c>. A parser that set them would be a second place that
/// spelling could drift.
/// </para>
/// </remarks>
public abstract record IngestionEvent;

/// <summary>A merge request is open (or was re-observed open — labelled, pushed, reopened).</summary>
/// <param name="RepositoryExternalId">The repository's provider-side identifier.</param>
/// <param name="ExternalMrId">The merge request's provider-side identifier.</param>
/// <param name="SourceBranch">The branch being merged from.</param>
/// <param name="TargetBranch">The branch being merged into.</param>
/// <param name="Title">The merge request title — a candidate the task-key rule may read from.</param>
/// <param name="Labels">
/// The merge request's labels, already normalized through <see cref="Core.Parsing.LabelSet"/> so
/// two spellings of the same label are one value — the form the readiness gate compares against, and
/// another candidate the task-key rule may read from.
/// </param>
/// <param name="ProviderEventId">
/// The provider's own delivery id, for dedup, or empty when the provider does not supply one. The
/// host combines it with the source to form the inbox key; empty means "not deduplicated".
/// </param>
/// <remarks>
/// The parser no longer resolves the task key: which field it comes from and by what pattern is a
/// per-connection rule, and the parser (in the ingress) cannot see connection settings. It carries
/// the raw candidates instead, and the consumer — which has the connection — extracts the key.
/// </remarks>
public sealed record MrOpenedEvent(
    string RepositoryExternalId,
    string ExternalMrId,
    string SourceBranch,
    string TargetBranch,
    string? Title,
    IReadOnlyList<string> Labels,
    string ProviderEventId,
    // The latest pipeline result when the source knows it; null from a webhook, which does not carry
    // pipeline status. Populated by the poll path, which reads the merge request from the API.
    string? PipelineResult = null) : IngestionEvent;

/// <summary>A merge request changed to a non-open state (merged, closed).</summary>
/// <param name="RepositoryExternalId">The repository's provider-side identifier.</param>
/// <param name="ExternalMrId">The merge request's provider-side identifier.</param>
/// <param name="NewStatus">The normalized status.</param>
/// <param name="Labels">
/// The labels as observed with this change, normalized. Carried because readiness is label-driven
/// and a status change is one of the moments labels are re-observed; empty when the provider's
/// payload for this event does not include them.
/// </param>
/// <param name="ProviderEventId">The provider's delivery id for dedup, or empty.</param>
/// <remarks>
/// There is no observed-at field: the change time is the receiver's clock, which only the host
/// holds (as a <c>TimeProvider</c>), and the parser is pure. The host stamps it when it maps this
/// to the wire message — the same clock the webhook front door has always used.
/// </remarks>
public sealed record MrStatusChangedEvent(
    string RepositoryExternalId,
    string ExternalMrId,
    MergeRequestStatus NewStatus,
    IReadOnlyList<string> Labels,
    string ProviderEventId) : IngestionEvent;

/// <summary>A task (tracker issue) was created.</summary>
/// <param name="ExternalId">The task's provider-side key.</param>
/// <param name="Title">The task title.</param>
/// <param name="ProviderEventId">The provider's delivery id for dedup, or empty.</param>
public sealed record TaskCreatedEvent(
    string ExternalId,
    string Title,
    string ProviderEventId) : IngestionEvent;

/// <summary>A task's status changed.</summary>
/// <param name="ExternalId">The task's provider-side key.</param>
/// <param name="NewStatus">The provider's status string, normalized by the consumer, not here.</param>
/// <param name="Closed">
/// Whether this change moved the task into a closed status. The decision is the provider's — only
/// the provider knows which of its statuses count as closed — but the <b>time</b> of closure is the
/// receiver's clock, which the host holds, so the parser reports the decision and the host stamps
/// the timestamp.
/// </param>
/// <param name="ProviderEventId">The provider's delivery id for dedup, or empty.</param>
public sealed record TaskStatusChangedEvent(
    string ExternalId,
    string NewStatus,
    bool Closed,
    string ProviderEventId) : IngestionEvent;
