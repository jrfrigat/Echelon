using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace ReleaseOrchestrator.Ingress.Webhooks.Endpoints;

public static class WebhookTokens
{
    /// <summary>
    /// Constant-time shared-secret check. Fails closed when no secret is configured, so a
    /// connection without a token rejects everything rather than accepting anything.
    /// </summary>
    public static bool Matches(string? presented, string? expected)
    {
        if (string.IsNullOrEmpty(expected) || string.IsNullOrEmpty(presented))
            return false;

        var a = Encoding.UTF8.GetBytes(presented);
        var b = Encoding.UTF8.GetBytes(expected);

        // FixedTimeEquals requires equal lengths; comparing lengths first would leak the
        // secret's length, so hash both sides to a fixed width and compare those.
        return CryptographicOperations.FixedTimeEquals(SHA256.HashData(a), SHA256.HashData(b));
    }
}

public static class WebhookConnectionName
{
    // ':' delimits the configuration hierarchy, so an unfiltered name from the URL is
    // interpolated straight into a config key. No exploit exists against the current key
    // shape, but the pattern is one refactor away from being one.
    private static readonly Regex Allowed = new(
        "^[A-Za-z0-9_-]{1,100}$", RegexOptions.Compiled, TimeSpan.FromMilliseconds(50));

    /// <returns>The name if it is well-formed, otherwise <c>null</c>.</returns>
    public static string? Sanitize(string? connectionName) =>
        connectionName is not null && Allowed.IsMatch(connectionName) ? connectionName : null;
}
