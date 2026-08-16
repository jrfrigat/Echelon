using System.Text.RegularExpressions;

namespace ReleaseOrchestrator.Ingress.Webhooks.Endpoints;

/// <summary>
/// Validates the connection name a webhook route carries before it is used as a configuration key.
/// </summary>
/// <remarks>
/// The token comparison that used to live beside this has moved to the providers - each provider
/// knows which header carries its secret and how to check it (a shared secret today, an HMAC
/// tomorrow), and they share the constant-time comparison through
/// <c>Providers.Abstractions.Ingestion.WebhookSignatures</c>. What stays here is host-only: the
/// route parameter is interpolated into a config key, and <c>:</c> delimits the config hierarchy, so
/// the name is validated before it can reach that key.
/// </remarks>
public static class WebhookConnectionName
{
    // ':' delimits the configuration hierarchy, so an unfiltered name from the URL would be
    // interpolated straight into a config key. No exploit exists against the current key shape, but
    // the pattern is one refactor away from being one.
    private static readonly Regex Allowed = new(
        "^[A-Za-z0-9_-]{1,100}$", RegexOptions.Compiled, TimeSpan.FromMilliseconds(50));

    /// <returns>The name if it is well-formed, otherwise <c>null</c>.</returns>
    public static string? Sanitize(string? connectionName) =>
        connectionName is not null && Allowed.IsMatch(connectionName) ? connectionName : null;
}
