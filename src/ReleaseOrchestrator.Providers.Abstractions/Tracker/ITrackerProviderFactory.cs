using ReleaseOrchestrator.Core.Entities;

namespace ReleaseOrchestrator.Providers.Abstractions.Tracker;

/// <summary>
/// Turns a stored tracker connection into a working provider.
/// </summary>
public interface ITrackerProviderFactory
{
    /// <summary>The provider types that are registered, canonical form, for validation and error messages.</summary>
    IReadOnlyCollection<string> AvailableProviders { get; }

    /// <summary>
    /// The provider-specific settings the named provider accepts, so a UI can render a form for a
    /// provider it has never heard of.
    /// </summary>
    /// <param name="providerType">Provider type; matched in canonical form.</param>
    /// <returns>The schema, or an empty list when the provider needs no settings.</returns>
    /// <exception cref="UnknownProviderException">No adapter is registered for that type.</exception>
    IReadOnlyList<ProviderSettingSchema> GetSettingsSchema(string providerType);

    /// <summary>Creates a provider bound to <paramref name="connection"/>.</summary>
    /// <param name="connection">The stored connection.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A provider ready to serve calls for that connection.</returns>
    /// <exception cref="UnknownProviderException">
    /// No adapter is registered for the connection's provider type. The message lists those that are.
    /// </exception>
    Task<ITrackerProvider> CreateAsync(TrackerConnection connection, CancellationToken ct);
}
