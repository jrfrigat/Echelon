using ReleaseOrchestrator.Infrastructure.Persistence.Models;
using ReleaseOrchestrator.Providers.Abstractions.Tracker;
using ReleaseOrchestrator.Providers.Abstractions.Vcs;

namespace ReleaseOrchestrator.Infrastructure.Providers;

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
            connection.EncryptedAccessToken,
            connection.ReadyForDeployLabel);

    public static TrackerConnectionDescriptor ToDescriptor(this TrackerConnection connection) =>
        new(connection.Name,
            connection.ProviderType,
            connection.ApiUrl,
            connection.EncryptedAccessToken,
            connection.ProviderSettingsJson);
}
