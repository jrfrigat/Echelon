using ReleaseOrchestrator.Core.Entities;

namespace ReleaseOrchestrator.Providers.Abstractions.Vcs;

/// <summary>
/// Turns a stored connection into a working provider.
/// </summary>
/// <remarks>
/// The single place that maps <c>VcsConnection.ProviderType</c> to an adapter. Callers pass the
/// connection they already loaded and get something they can use; which adapter served it, and
/// how its token was decrypted, is not their problem.
/// </remarks>
public interface IVcsProviderFactory
{
    /// <summary>
    /// The provider types that are registered, canonical form, for validation and error messages.
    /// </summary>
    /// <remarks>
    /// Exposed so the API can reject an unknown provider type at the point an operator submits it
    /// rather than storing a row that fails on first use.
    /// </remarks>
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
    Task<IVcsProvider> CreateAsync(VcsConnection connection, CancellationToken ct);
}
