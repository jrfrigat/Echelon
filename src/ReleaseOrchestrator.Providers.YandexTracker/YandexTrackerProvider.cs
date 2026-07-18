using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using ReleaseOrchestrator.Providers.Abstractions.Tracker;

namespace ReleaseOrchestrator.Providers.YandexTracker;

/// <summary>
/// Yandex.Tracker, bound to one connection.
/// </summary>
/// <remarks>
/// Implements <see cref="ITrackerDependencySource"/> because this tracker does model issue links.
/// A tracker that did not would simply not implement it, and callers would see that with an
/// <c>is</c> check rather than by calling and getting an empty list back — which is
/// indistinguishable from an issue that genuinely has no dependencies.
/// </remarks>
internal sealed class YandexTrackerProvider(
    HttpClient http,
    TrackerProviderContext context,
    YandexTrackerOptions options) : ITrackerProvider, ITrackerDependencySource, ITrackerMutator
{
    /// <inheritdoc/>
    public TrackerCapabilities Capabilities => TrackerCapabilities.None;

    /// <inheritdoc/>
    public bool IsClosedStatus(string? statusKey) => YandexTrackerStatusRules.IsClosed(statusKey);

    /// <inheritdoc/>
    public async Task<TrackerIssue?> GetIssueAsync(string issueKey, CancellationToken ct)
    {
        using var request = Authorized(HttpMethod.Get, Url($"v2/issues/{Uri.EscapeDataString(issueKey)}"));

        var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return null;

        var dto = await response.Content.ReadFromJsonAsync<YtIssueDto>(cancellationToken: ct).ConfigureAwait(false);

        return dto is null
            ? null
            : new TrackerIssue(
                dto.Key,
                dto.Summary ?? string.Empty,
                dto.Status?.Key ?? string.Empty,
                dto.ResolvedAt?.UtcDateTime);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<TrackerIssueDependency>> GetIssueDependenciesAsync(string issueKey, CancellationToken ct)
    {
        using var request = Authorized(HttpMethod.Get, Url($"v2/issues/{Uri.EscapeDataString(issueKey)}/links"));

        var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return [];

        var dtos = await response.Content
            .ReadFromJsonAsync<List<YtIssueLinkDto>>(cancellationToken: ct)
            .ConfigureAwait(false);

        // Only "depends": it is the one relation that carries ordering. "relates" exists too and
        // means nothing — feeding it to a topological sort invents constraints nobody stated.
        //
        // UNVERIFIED — direction. Atlassian is explicit that a link's direction is interpretable
        // only at the UI level, and Yandex.Tracker follows Jira's model, so "depends on" and "is
        // dependent by" are plausibly the same link seen from two ends. This carries forward the
        // existing behaviour (the linked issue is the prerequisite) rather than changing it
        // blind: no live tracker was reachable to confirm, and inverting an edge on a guess would
        // reverse the deploy order in exactly the case this product exists for. Check the
        // "direction" field against a live API before trusting this with more than one link type.
        return dtos?
            .Where(d => string.Equals(d.Type?.Id, "depends", StringComparison.Ordinal))
            .Where(d => d.Object?.Key is { Length: > 0 })
            .Select(d => new TrackerIssueDependency(issueKey, d.Object!.Key))
            .ToList() ?? [];
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Yandex.Tracker changes status through a transition, not a status field: this lists the
    /// available transitions, finds the one whose target is the requested status, and executes it.
    /// NOT VERIFIED against a live tracker.
    /// </remarks>
    public async Task SetStatusAsync(string issueKey, string statusKey, CancellationToken ct)
    {
        using var listRequest = Authorized(HttpMethod.Get, Url($"v2/issues/{Uri.EscapeDataString(issueKey)}/transitions"));
        var listResponse = await http.SendAsync(listRequest, ct).ConfigureAwait(false);
        listResponse.EnsureSuccessStatusCode();

        var transitions = await listResponse.Content
            .ReadFromJsonAsync<List<YtTransitionDto>>(cancellationToken: ct).ConfigureAwait(false) ?? [];
        var match = transitions.FirstOrDefault(t => string.Equals(t.To?.Key, statusKey, StringComparison.OrdinalIgnoreCase));
        if (match?.Id is not { Length: > 0 } transitionId)
            throw new InvalidOperationException($"No transition to status '{statusKey}' is available on issue {issueKey}.");

        using var executeRequest = Authorized(HttpMethod.Post,
            Url($"v2/issues/{Uri.EscapeDataString(issueKey)}/transitions/{Uri.EscapeDataString(transitionId)}/_execute"));
        var executeResponse = await http.SendAsync(executeRequest, ct).ConfigureAwait(false);
        executeResponse.EnsureSuccessStatusCode();
    }

    /// <inheritdoc/>
    /// <remarks>NOT VERIFIED against a live tracker.</remarks>
    public async Task AddCommentAsync(string issueKey, string comment, CancellationToken ct)
    {
        using var request = Authorized(HttpMethod.Post, Url($"v2/issues/{Uri.EscapeDataString(issueKey)}/comments"));
        request.Content = JsonContent.Create(new { text = comment });

        var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
    }

    private Uri Url(string relative) => new($"{context.ApiUrl.ToString().TrimEnd('/')}/{relative}");

    private HttpRequestMessage Authorized(HttpMethod method, Uri url)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("OAuth", context.AccessToken);
        request.Headers.Add("X-Org-Id", options.OrgId);
        return request;
    }

    private sealed record YtIssueDto(
        [property: JsonPropertyName("key")] string Key,
        [property: JsonPropertyName("summary")] string? Summary,
        [property: JsonPropertyName("status")] YtStatus? Status,
        // DateTimeOffset, not DateTime: the tracker stamps an offset, and deserialising that into a
        // DateTime yields Kind=Local — which SQL Server stores without complaint and PostgreSQL
        // refuses outright, since it maps DateTime to timestamptz and Npgsql writes only Kind=Utc.
        // The ambiguity is removed here rather than compensated for later: an offset is exactly what
        // DateTimeOffset is for, and the adapter is where the tracker's format is known.
        [property: JsonPropertyName("resolvedAt")] DateTimeOffset? ResolvedAt);

    private sealed record YtStatus([property: JsonPropertyName("key")] string Key);

    private sealed record YtIssueLinkDto(
        [property: JsonPropertyName("type")] YtLinkType? Type,
        [property: JsonPropertyName("object")] YtLinkObject? Object);

    private sealed record YtLinkType([property: JsonPropertyName("id")] string Id);

    private sealed record YtLinkObject([property: JsonPropertyName("key")] string Key);

    private sealed record YtTransitionDto(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("to")] YtTransitionTo? To);

    private sealed record YtTransitionTo([property: JsonPropertyName("key")] string? Key);
}
