using Echelon.Infrastructure.Persistence.Models;
using Echelon.Providers.Abstractions.Tracker;
using Echelon.Providers.Abstractions.Vcs;

namespace Echelon.Infrastructure.Providers;

/// <summary>
/// Reads a stored connection into what the provider factory takes.
/// </summary>
/// <remarks>
/// The seam that keeps the persistence model out of the provider contracts: an adapter package
/// describes what it needs, and this is the only place that knows a database row can supply it.
/// </remarks>
internal static class ConnectionDescriptors
{
    public static VcsConnectionDescriptor ToDescriptor(this VcsConnection connection) =>
        new(connection.Name,
            connection.ProviderType,
            connection.ApiUrl,
            connection.EncryptedAccessToken);

    public static TrackerConnectionDescriptor ToDescriptor(this TrackerConnection connection) =>
        new(connection.Name,
            connection.ProviderType,
            connection.ApiUrl,
            connection.EncryptedAccessToken,
            connection.ProviderSettingsJson);
}
