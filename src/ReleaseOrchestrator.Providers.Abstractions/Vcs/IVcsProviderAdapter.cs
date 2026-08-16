namespace ReleaseOrchestrator.Providers.Abstractions.Vcs;

/// <summary>
/// Builds an <see cref="IVcsProvider"/> for one connection. One implementation per VCS.
/// </summary>
/// <remarks>
/// <para>
/// This is the type registered as a keyed service - the key being the provider type from the
/// database - and it exists so that binding a provider to a connection is a distinct, awaitable
/// step. Renovate splits the same seam: <c>initPlatform</c> settles endpoint, credentials and
/// server version before any repository call happens.
/// </para>
/// <para>
/// Two interfaces rather than one because construction can do I/O - detecting a self-hosted
/// server's version needs a round trip - and an <see cref="IVcsProvider"/> handed to a caller
/// must already be usable. Folding an <c>InitializeAsync</c> into the provider itself would make
/// "not initialized yet" a state every caller could observe and every adapter would have to guard.
/// </para>
/// </remarks>
public interface IVcsProviderAdapter
{
    /// <summary>
    /// The provider-specific settings a connection to this VCS may carry. Empty when it needs
    /// none - GitLab does not, which is exactly why the API must not assume every provider has
    /// the same fields.
    /// </summary>
    IReadOnlyList<ProviderSettingSchema> SettingsSchema => [];

    /// <summary>Binds this adapter to a connection, detecting whatever the install has to be asked about.</summary>
    /// <param name="context">The connection's endpoint, credentials and label configuration.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A provider ready to serve calls for that connection.</returns>
    Task<IVcsProvider> ConnectAsync(VcsProviderContext context, CancellationToken ct);
}
