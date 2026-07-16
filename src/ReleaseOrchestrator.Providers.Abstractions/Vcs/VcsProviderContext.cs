namespace ReleaseOrchestrator.Providers.Abstractions.Vcs;

/// <summary>
/// Everything an adapter needs to bind itself to one VCS connection.
/// </summary>
/// <remarks>
/// <para>
/// Handed to the adapter once, at connect time, instead of being threaded through every call.
/// The ports used to read <c>GetMergeRequestAsync(apiUrl, token, projectPath, iid, ct)</c>: the
/// caller had to know which pieces of configuration the provider wanted, which is the abstraction
/// failing to abstract. Renovate binds credentials in <c>initPlatform</c> and go-scm keeps
/// <c>BaseURL</c> on the client for the same reason.
/// </para>
/// <para>
/// Provider-specific configuration is deliberately absent. It belongs in typed options inside the
/// adapter that needs it, never in a contract every provider has to implement.
/// </para>
/// </remarks>
/// <param name="ConnectionName">The connection's name, for log lines and error messages.</param>
/// <param name="ApiUrl">Base address of the provider's API.</param>
/// <param name="AccessToken">
/// The decrypted access token. Decryption happens in the factory so that adapters never touch
/// the data-protection stack, and so a token is never stored on a provider-facing entity in clear.
/// </param>
/// <param name="ReadyForDeployLabel">
/// The label that marks a merge request deployable, or <c>null</c> when this connection does not
/// use label-driven promotion.
/// </param>
public sealed record VcsProviderContext(
    string ConnectionName,
    Uri ApiUrl,
    string AccessToken,
    string? ReadyForDeployLabel);
