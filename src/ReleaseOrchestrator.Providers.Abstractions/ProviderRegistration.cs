using ReleaseOrchestrator.Core.Enums;

namespace ReleaseOrchestrator.Providers.Abstractions;

/// <summary>
/// Declares that a VCS provider is registered under <paramref name="ProviderType"/>, and how its
/// merge-request events arrive.
/// </summary>
/// <remarks>
/// Keyed DI can resolve a service by key but cannot enumerate the keys that exist. Without that
/// list a misspelled provider type could only produce "not found", so every adapter also
/// registers one of these: it is what lets the factory answer "must be one of: gitlab-webhook,
/// gitlab-poll" and what lets the API validate a provider type before writing it to the database.
/// Renovate's <c>getPlatformList()</c> exists for the same reason.
///
/// <paramref name="Ingestion"/> is what replaced the per-connection <c>IngestionMode</c> column:
/// push versus poll is now a property of the provider TYPE, not a toggle on each connection, so a
/// GitLab install that pushes and one that must be polled are two distinct types. The poller sweeps
/// exactly the connections whose type is registered <see cref="IngestionMode.Poll"/>.
/// </remarks>
/// <param name="ProviderType">The canonical key, as produced by <see cref="ProviderKey.Normalize"/>.</param>
/// <param name="Ingestion">How this type's events arrive: pushed by webhook, or pulled by the poller.</param>
/// <param name="Description">A short, human sentence for the admin "installed plugins" view; null when none.</param>
public sealed record VcsProviderRegistration(
    string ProviderType, IngestionMode Ingestion = IngestionMode.Push, string? Description = null);

/// <summary>
/// Declares that a tracker provider is registered under <paramref name="ProviderType"/>.
/// </summary>
/// <param name="ProviderType">The canonical key, as produced by <see cref="ProviderKey.Normalize"/>.</param>
/// <param name="Description">A short, human sentence for the admin "installed plugins" view; null when none.</param>
/// <seealso cref="VcsProviderRegistration"/>
public sealed record TrackerProviderRegistration(string ProviderType, string? Description = null);
