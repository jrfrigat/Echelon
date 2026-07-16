using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ReleaseOrchestrator.Core.Entities;
using ReleaseOrchestrator.Infrastructure.Auth;
using ReleaseOrchestrator.Providers.Abstractions;
using ReleaseOrchestrator.Providers.Abstractions.Tracker;

namespace ReleaseOrchestrator.Infrastructure.Providers;

/// <summary>
/// Resolves the adapter a tracker connection names and binds it to that connection.
/// </summary>
/// <remarks>See <see cref="VcsProviderFactory"/> — the same shape, for the same reasons.</remarks>
internal sealed class TrackerProviderFactory(
    IServiceProvider services,
    IEnumerable<TrackerProviderRegistration> registrations,
    TokenProtector protector) : ITrackerProviderFactory
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
            throw new UnknownProviderException("Tracker", providerType, _available);

        return services.GetRequiredKeyedService<ITrackerProviderAdapter>(key).SettingsSchema;
    }

    /// <inheritdoc/>
    public async Task<ITrackerProvider> CreateAsync(TrackerConnection connection, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(connection);

        var key = ProviderKey.Normalize(connection.ProviderType);

        if (!_available.Contains(key))
            throw new UnknownProviderException("Tracker", connection.ProviderType, _available);

        var adapter = services.GetRequiredKeyedService<ITrackerProviderAdapter>(key);

        if (!Uri.TryCreate(connection.ApiUrl, UriKind.Absolute, out var apiUrl))
            throw new InvalidOperationException(
                $"Tracker connection '{connection.Name}' has an ApiUrl that is not an absolute URL: '{connection.ApiUrl}'.");

        var context = new TrackerProviderContext(
            ConnectionName: connection.Name,
            ApiUrl: apiUrl,
            AccessToken: protector.Unprotect(connection.EncryptedAccessToken),
            ProviderSettings: ProviderSettings.Parse(connection.ProviderSettingsJson, connection.Name));

        return await adapter.ConnectAsync(context, ct).ConfigureAwait(false);
    }
}

/// <summary>Reads a connection's provider-specific settings bag.</summary>
internal static class ProviderSettings
{
    private static readonly IReadOnlyDictionary<string, string?> Empty =
        new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Parses the stored JSON object into a case-insensitive bag.</summary>
    /// <param name="json">The stored JSON, or null when the connection has no settings.</param>
    /// <param name="connectionName">Named in the error, so a bad row can be found.</param>
    /// <returns>The settings; empty when there are none.</returns>
    /// <exception cref="InvalidOperationException">The column holds something that is not a JSON object.</exception>
    internal static IReadOnlyDictionary<string, string?> Parse(string? json, string connectionName)
    {
        if (string.IsNullOrWhiteSpace(json)) return Empty;

        try
        {
            var parsed = JsonSerializer.Deserialize<Dictionary<string, string?>>(json);
            return parsed is null
                ? Empty
                : new Dictionary<string, string?>(parsed, StringComparer.OrdinalIgnoreCase);
        }
        catch (JsonException ex)
        {
            // Thrown rather than swallowed: silently treating a malformed bag as "no settings"
            // would surface as a missing organization id from a connection that plainly has one.
            throw new InvalidOperationException(
                $"Connection '{connectionName}' has provider settings that are not a JSON object of strings.", ex);
        }
    }
}
