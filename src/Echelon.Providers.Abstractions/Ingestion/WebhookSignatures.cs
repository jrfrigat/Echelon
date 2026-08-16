using System.Security.Cryptography;
using System.Text;

namespace Echelon.Providers.Abstractions.Ingestion;

/// <summary>
/// Constant-time credential checks a webhook parser can share, so verification does not leak a
/// secret through timing.
/// </summary>
/// <remarks>
/// This is pure BCL cryptography with no HTTP and no I/O - the same bar as the other helpers this
/// contracts assembly already carries (<see cref="ProviderKey.Normalize"/>,
/// <see cref="ProviderSettingsBag.Validate"/>). It lives here rather than in the host because
/// verification is the provider's job (a provider decides whether it authenticates by a shared
/// secret in a header or an HMAC over the body), and every provider must get the timing-safe
/// comparison right the same way rather than each writing its own.
/// </remarks>
public static class WebhookSignatures
{
    /// <summary>
    /// A constant-time shared-secret comparison.
    /// </summary>
    /// <param name="presented">The value the request carried, e.g. a token header.</param>
    /// <param name="expected">The configured secret.</param>
    /// <returns>
    /// True only when both are non-empty and equal. Fails closed: an unconfigured secret rejects
    /// every delivery rather than accepting any.
    /// </returns>
    /// <remarks>
    /// Both sides are hashed to a fixed width before comparison because
    /// <see cref="CryptographicOperations.FixedTimeEquals(ReadOnlySpan{byte}, ReadOnlySpan{byte})"/>
    /// requires equal lengths, and comparing the raw lengths first would leak the secret's length.
    /// </remarks>
    public static bool FixedTimeEquals(string? presented, string? expected)
    {
        if (string.IsNullOrEmpty(expected) || string.IsNullOrEmpty(presented))
            return false;

        var a = SHA256.HashData(Encoding.UTF8.GetBytes(presented));
        var b = SHA256.HashData(Encoding.UTF8.GetBytes(expected));
        return CryptographicOperations.FixedTimeEquals(a, b);
    }
}
