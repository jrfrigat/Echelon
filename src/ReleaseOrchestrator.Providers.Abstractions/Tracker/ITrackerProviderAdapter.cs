namespace ReleaseOrchestrator.Providers.Abstractions.Tracker;

/// <summary>
/// Builds an <see cref="ITrackerProvider"/> for one connection. One implementation per tracker.
/// </summary>
/// <remarks>
/// Registered as a keyed service, keyed by the provider type stored on the connection. See
/// <see cref="Vcs.IVcsProviderAdapter"/> for why binding is a separate, awaitable step.
/// </remarks>
public interface ITrackerProviderAdapter
{
    /// <summary>Binds this adapter to a connection.</summary>
    /// <param name="context">The connection's endpoint, credentials and provider-specific settings.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A provider ready to serve calls for that connection.</returns>
    /// <exception cref="InvalidOperationException">
    /// The connection is missing a setting this provider requires.
    /// </exception>
    Task<ITrackerProvider> ConnectAsync(TrackerProviderContext context, CancellationToken ct);
}
