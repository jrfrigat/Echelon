namespace ReleaseOrchestrator.Providers.Abstractions.Ingestion;

/// <summary>
/// The provider-specific strings the host needs to mount a provider's webhook: what its route
/// segment is, where its per-connection secret is configured, and how its events identify their
/// producer.
/// </summary>
/// <remarks>
/// <para>
/// These four strings were hardcoded in the ingress, once per provider, which is what made "add a
/// provider" a change to the host. Declaring them here moves them to the provider that owns them,
/// and - just as importantly - lets each provider keep the exact strings it already uses, so this
/// refactor changes no route and no config key.
/// </para>
/// <para>
/// They are separate fields rather than one derived from <see cref="ProviderType"/> on purpose. The
/// tracker provider spells itself four ways in production today: route <c>tracker</c>, config
/// section <c>Tracker</c>, source prefix <c>yandex</c>, registered key <c>yandextracker-webhook</c>.
/// Deriving any of them from the key would rename a live route, or a live config key, or orphan the
/// dedup rows keyed by the old source prefix. Keeping them independent preserves all of it;
/// unifying them is a deliberate, separately migrated decision, not a side effect of this change.
/// </para>
/// </remarks>
/// <param name="ProviderType">
/// The canonical provider key (as <see cref="ProviderKey.Normalize"/> produces), under which the
/// parser is registered and resolved. Not necessarily equal to <see cref="RouteSegment"/>.
/// </param>
/// <param name="RouteSegment">
/// The path segment the endpoint mounts at: <c>/webhooks/{RouteSegment}/{connectionName}</c>. Must
/// be URL-safe and unique across providers; the host mounts one route per registered descriptor.
/// </param>
/// <param name="SecretConfigSection">
/// The configuration section under <c>Webhooks:</c> holding per-connection tokens, so the secret
/// for a connection is read from <c>Webhooks:{SecretConfigSection}:{connectionName}:Token</c>.
/// </param>
/// <param name="SourcePrefix">
/// The prefix stamped onto every event's <c>Source</c> as <c>"{SourcePrefix}/{connectionName}"</c>.
/// Half of the dedup inbox key, so a change to it re-processes past events - which is why it is
/// declared and preserved rather than derived.
/// </param>
public sealed record WebhookEndpointDescriptor(
    string ProviderType,
    string RouteSegment,
    string SecretConfigSection,
    string SourcePrefix);
