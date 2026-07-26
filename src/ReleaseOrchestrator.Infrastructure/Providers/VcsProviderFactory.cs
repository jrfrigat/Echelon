using Microsoft.Extensions.DependencyInjection;
using ReleaseOrchestrator.Infrastructure.Persistence.Models;
using ReleaseOrchestrator.Infrastructure.Auth;
using ReleaseOrchestrator.Providers.Abstractions;
using ReleaseOrchestrator.Providers.Abstractions.Vcs;

namespace ReleaseOrchestrator.Infrastructure.Providers;

/// <summary>
/// Resolves the adapter a VCS connection names and binds it to that connection.
/// </summary>
/// <remarks>
/// <para>
/// The keyed lookup is hidden in here on purpose. The key is <c>VcsConnection.ProviderType</c>,
/// which is a database column, so it is not known until a row is read — <c>[FromKeyedServices]</c>
/// cannot express that, since its key is baked in at compile time. A <c>GetRequiredKeyedService</c>
/// behind a factory is the shape that fits a runtime key.
/// </para>
/// <para>
/// Token decryption lives here too, so that no adapter references the data-protection stack and
/// no adapter can be handed a connection it would have to decrypt itself.
/// </para>
/// </remarks>
internal sealed class VcsProviderFactory(
    IServiceProvider services,
    IEnumerable<VcsProviderRegistration> registrations,
    TokenProtector protector) : IVcsProviderFactory
{
    private readonly IReadOnlyCollection<string> _available =
        registrations.Select(r => ProviderKey.Normalize(r.ProviderType)).Distinct(StringComparer.Ordinal).ToList();

    /// <inheritdoc/>
    public IReadOnlyCollection<string> AvailableProviders => _available;

    /// <inheritdoc/>
    public IReadOnlyList<ProviderSettingSchema> GetSettingsSchema(string providerType)
    {
        var key = ProviderKey.Normalize(providerType);

        if (!_available.Contains(key))
            throw new UnknownProviderException("VCS", providerType, _available);

        return services.GetRequiredKeyedService<IVcsProviderAdapter>(key).SettingsSchema;
    }

    /// <inheritdoc/>
    public async Task<IVcsProvider> CreateAsync(VcsConnectionDescriptor connection, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var key = ProviderKey.Normalize(connection.ProviderType);

        // Checked before the resolve so the error names the alternatives. GetRequiredKeyedService
        // would throw too, but its message says only that a service is missing — useless to an
        // operator who mistyped a provider name into a connection form.
        if (!_available.Contains(key))
            throw new UnknownProviderException("VCS", connection.ProviderType, _available);

        var adapter = services.GetRequiredKeyedService<IVcsProviderAdapter>(key);

        if (!Uri.TryCreate(connection.ApiUrl, UriKind.Absolute, out var apiUrl))
            throw new InvalidOperationException(
                $"VCS connection '{connection.Name}' has an ApiUrl that is not an absolute URL: '{connection.ApiUrl}'.");

        var context = new VcsProviderContext(
            ConnectionName: connection.Name,
            ApiUrl: apiUrl,
            AccessToken: protector.Unprotect(connection.EncryptedAccessToken));

        return await adapter.ConnectAsync(context, ct).ConfigureAwait(false);
    }
}
