namespace ReleaseOrchestrator.Providers.Abstractions;

/// <summary>
/// Declares that a VCS provider is registered under <paramref name="ProviderType"/>.
/// </summary>
/// <remarks>
/// Keyed DI can resolve a service by key but cannot enumerate the keys that exist. Without that
/// list a misspelled provider type could only produce "not found", so every adapter also
/// registers one of these: it is what lets the factory answer "must be one of: gitlab" and what
/// lets the API validate a provider type before writing it to the database. Renovate's
/// <c>getPlatformList()</c> exists for the same reason.
/// </remarks>
/// <param name="ProviderType">The canonical key, as produced by <see cref="ProviderKey.Normalize"/>.</param>
public sealed record VcsProviderRegistration(string ProviderType);

/// <summary>
/// Declares that a tracker provider is registered under <paramref name="ProviderType"/>.
/// </summary>
/// <param name="ProviderType">The canonical key, as produced by <see cref="ProviderKey.Normalize"/>.</param>
/// <seealso cref="VcsProviderRegistration"/>
public sealed record TrackerProviderRegistration(string ProviderType);
