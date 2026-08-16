namespace ReleaseOrchestrator.Providers.Abstractions.Tracker;

/// <summary>
/// Everything an adapter needs to bind itself to one tracker connection.
/// </summary>
/// <param name="ConnectionName">The connection's name, for log lines and error messages.</param>
/// <param name="ApiUrl">Base address of the tracker's API.</param>
/// <param name="AccessToken">The decrypted access token; decryption stays in the factory.</param>
/// <param name="ProviderSettings">
/// Opaque provider-specific settings, read by the adapter that understands them and by nothing
/// else.
/// </param>
/// <remarks>
/// <see cref="ProviderSettings"/> is how a provider's own configuration reaches it without
/// appearing in a shared contract. Yandex.Tracker needs an organization id; Jira will need
/// something else. Putting <c>orgId</c> in the common context would recreate exactly the leak
/// this assembly removes - so the adapter parses what it needs, and a missing value is its own
/// error to raise.
/// </remarks>
public sealed record TrackerProviderContext(
    string ConnectionName,
    Uri ApiUrl,
    string AccessToken,
    IReadOnlyDictionary<string, string?> ProviderSettings);
