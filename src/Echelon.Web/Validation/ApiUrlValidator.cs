using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Localization;
using Echelon.Web.Resources;

namespace Echelon.Web.Validation;

/// <summary>
/// Vets an operator-supplied VCS/tracker base URL before it is stored.
///
/// These URLs are fetched server-side by background sync, so an unchecked value turns the
/// app into a probe for its own network (cloud metadata endpoints, the broker's management
/// UI, Redis). Requires https and rejects literal private, loopback and link-local hosts.
///
/// Limits worth knowing: a hostname resolving to a private address is not caught here, and
/// DNS can change after validation. Set Security:AllowedApiHosts to pin an allow-list where
/// that matters.
/// </summary>
public static class ApiUrlValidator
{
    /// <param name="localizer">
    /// Supplies the rejection wording. Passed in rather than resolved here because this stays a
    /// static helper: the callers are controllers, which already hold the localizer.
    /// </param>
    public static bool TryValidate(
        string? apiUrl,
        IReadOnlyCollection<string> allowedHosts,
        IStringLocalizer<ApiStrings> localizer,
        out string error)
    {
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(apiUrl))
        {
            error = localizer["Url_Required"];
            return false;
        }

        if (!Uri.TryCreate(apiUrl, UriKind.Absolute, out var uri))
        {
            error = localizer["Url_MustBeAbsolute"];
            return false;
        }

        if (uri.Scheme != Uri.UriSchemeHttps)
        {
            error = localizer["Url_MustBeHttps"];
            return false;
        }

        if (allowedHosts.Count > 0
            && !allowedHosts.Any(h => string.Equals(h, uri.Host, StringComparison.OrdinalIgnoreCase)))
        {
            error = localizer["Url_HostNotAllowed", uri.Host];
            return false;
        }

        if (IPAddress.TryParse(uri.Host, out var ip) && IsInternal(ip))
        {
            error = localizer["Url_InternalAddress", uri.Host];
            return false;
        }

        return true;
    }

    private static bool IsInternal(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip)) return true;

        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
            return ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6UniqueLocal
                   || (ip.IsIPv4MappedToIPv6 && IsInternal(ip.MapToIPv4()));

        var b = ip.GetAddressBytes();
        return b[0] switch
        {
            10 => true,                                   // 10.0.0.0/8
            127 => true,                                  // 127.0.0.0/8
            172 => b[1] >= 16 && b[1] <= 31,              // 172.16.0.0/12
            192 => b[1] == 168,                           // 192.168.0.0/16
            169 => b[1] == 254,                           // 169.254.0.0/16 - cloud metadata
            0 => true,
            _ => false
        };
    }
}
