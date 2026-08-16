using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Echelon.Providers.GitLab;

/// <summary>
/// Detects and caches the version of a GitLab install.
/// </summary>
/// <remarks>
/// <para>
/// Needed now, with one provider, not later: GitLab.com is current by definition but a
/// self-hosted install is whatever version its owner last upgraded to, and the same adapter
/// serves both. Renovate carries the same detection for the same reason.
/// </para>
/// <para>
/// Cached per API address and held as a singleton: a connection's server version changes at most
/// when someone upgrades it, so asking on every provider construction would add a round trip to
/// every merge-request read to re-learn a constant.
/// </para>
/// </remarks>
public sealed class GitLabVersionDetector(ILogger<GitLabVersionDetector> logger)
{
    private readonly ConcurrentDictionary<string, GitLabServerVersion?> _cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Reads the server version, or returns a cached answer.</summary>
    /// <param name="http">Client to use; the caller has already applied the connection's credentials.</param>
    /// <param name="apiUrl">The install's base API address, which also keys the cache.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The version, or <c>null</c> when it could not be determined.</returns>
    /// <remarks>
    /// A failure is cached as null and logged once at warning. Undetectable is not fatal: the
    /// endpoint can be hidden by a proxy or forbidden to the token, and refusing to serve the
    /// connection at all would break a working install over a diagnostic. Callers treat null as
    /// "unknown" and fall back to conservative capabilities.
    /// </remarks>
    public async Task<GitLabServerVersion?> DetectAsync(HttpClient http, Uri apiUrl, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(apiUrl);

        var key = apiUrl.ToString();
        if (_cache.TryGetValue(key, out var cached)) return cached;

        var detected = await ReadVersionAsync(http, apiUrl, ct).ConfigureAwait(false);
        _cache[key] = detected;
        return detected;
    }

    private async Task<GitLabServerVersion?> ReadVersionAsync(HttpClient http, Uri apiUrl, CancellationToken ct)
    {
        var url = GitLabUrls.Version(apiUrl);

        try
        {
            var response = await http.GetAsync(url, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "GitLab at {ApiUrl} answered {Status} for its version; capabilities fall back to the "
                    + "conservative defaults.", apiUrl, (int)response.StatusCode);
                return null;
            }

            var dto = await response.Content
                .ReadFromJsonAsync<GitLabVersionDto>(cancellationToken: ct)
                .ConfigureAwait(false);

            if (GitLabServerVersion.TryParse(dto?.Version, out var version))
            {
                logger.LogInformation("GitLab at {ApiUrl} reports version {Version}", apiUrl, version.Raw);
                return version;
            }

            logger.LogWarning(
                "GitLab at {ApiUrl} reported an unparsable version '{Version}'; capabilities fall back to the "
                + "conservative defaults.", apiUrl, dto?.Version);
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or NotSupportedException)
        {
            // Deliberately not rethrown: see the remarks on DetectAsync. A cancelled request is
            // not caught here - that is the caller giving up, not the server being unreachable.
            logger.LogWarning(
                ex, "Could not read the version of the GitLab at {ApiUrl}; capabilities fall back to the "
                + "conservative defaults.", apiUrl);
            return null;
        }
    }

    private sealed record GitLabVersionDto([property: JsonPropertyName("version")] string? Version);
}
