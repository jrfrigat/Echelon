using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Echelon.Providers.Abstractions.Tracker;

namespace Echelon.Providers.YandexTracker;

/// <summary>
/// Yandex.Tracker, bound to one connection.
/// </summary>
/// <remarks>
/// Implements <see cref="ITrackerDependencySource"/> because this tracker does model issue links, and
/// <see cref="ITrackerIssueSource"/> because it can be searched - which is what lets a polled connection
/// find work that is not in the local database yet.
/// A tracker that did not would simply not implement it, and callers would see that with an
/// <c>is</c> check rather than by calling and getting an empty list back - which is
/// indistinguishable from an issue that genuinely has no dependencies.
/// </remarks>
internal sealed class YandexTrackerProvider(
    HttpClient http,
    TrackerProviderContext context,
    YandexTrackerOptions options) : ITrackerProvider, ITrackerDependencySource, ITrackerIssueSource, ITrackerMutator
{
    // Yandex.Tracker pages search results; 100 is its documented maximum per page. The sweep asks
    // for pages of this size until the caller's limit is met, so the limit is the only cap that
    // matters to a caller.
    private const int SearchPageSize = 100;

    /// <inheritdoc/>
    public TrackerCapabilities Capabilities => TrackerCapabilities.None;

    /// <inheritdoc/>
    /// <remarks>
    /// Reads the connection's own closed set, not the static default: which statuses mean "done" is
    /// a per-project decision. Both the sync consumer and the reconcile go through this method, so
    /// making it connection-scoped covers both at once.
    /// </remarks>
    public bool IsClosedStatus(string? statusKey) =>
        statusKey is not null && options.ClosedStatuses.Contains(statusKey.Trim());

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
                dto.ResolvedAt?.UtcDateTime,
                // The hierarchy comes back on the issue itself, not through /links: Yandex.Tracker
                // models "parent" as a field holding the parent issue, the way Jira does. A blank key
                // is treated as absent so a malformed object cannot resolve to a task named "".
                dto.Parent?.Key is { Length: > 0 } parentKey ? parentKey : null);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<TrackerIssueDependency>> GetIssueDependenciesAsync(string issueKey, CancellationToken ct)
    {
        using var request = Authorized(HttpMethod.Get, Url($"v2/issues/{Uri.EscapeDataString(issueKey)}/links"));

        var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        // Throw on a non-success status rather than returning an empty list. An empty list is
        // indistinguishable from "this issue has no dependencies", which the sync layer
        // (TrackerService.ReplaceDependenciesAsync) reads as an instruction to DELETE every existing
        // edge. A transient tracker error (500/502/429) or a 403 must instead propagate, so the sync
        // is redelivered and retried, never silently wiping a task's ordering constraints.
        response.EnsureSuccessStatusCode();

        var dtos = await response.Content
            .ReadFromJsonAsync<List<YtIssueLinkDto>>(cancellationToken: ct)
            .ConfigureAwait(false);

        // Only "depends": it is the one relation that carries ordering. "relates" exists too and
        // means nothing - feeding it to a topological sort invents constraints nobody stated.
        //
        // Only the OUTWARD end. Yandex.Tracker (Jira's model) returns the same relationship from both
        // issues: issue A's links carry {depends, direction: outward, object: B} and issue B's carry
        // {depends, direction: inward, object: A}. Emitting both - as this once did - produces A->B AND
        // B->A, a cycle that breaks the topological sort the moment both issues are synced. The outward
        // end is the dependent's own edge (A depends on B), so filtering to it records each dependency
        // exactly once, oriented as TrackerIssueDependency(dependent, prerequisite). The direction
        // semantics were not confirmable against a live tracker; if "outward" turns out to mean the
        // reverse, flip this single predicate - but one acyclic edge is correct where a cycle never was.
        return dtos?
            .Where(d => string.Equals(d.Type?.Id, "depends", StringComparison.Ordinal))
            .Where(d => string.Equals(d.Direction, "outward", StringComparison.OrdinalIgnoreCase))
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

    /// <inheritdoc/>
    /// <remarks>
    /// Yandex.Tracker is searched through <c>POST /v2/issues/_search</c> with a query in its own
    /// language, paged by <c>perPage</c> and <c>page</c>. What counts as open is the connection's own
    /// business - a queue list, or a query the operator wrote - because "open" is a workflow's word,
    /// not the API's; the default reading is "in these queues, with no resolution".
    /// NOT VERIFIED against a live tracker.
    /// </remarks>
    public async Task<IReadOnlyList<string>> ListOpenIssueKeysAsync(int limit, CancellationToken ct)
    {
        var query = options.BuildSearchQuery(context.ConnectionName);
        var keys = new List<string>();

        // Paged rather than one request: the API caps how much a page returns, so a single call would
        // silently truncate a connection whose queues hold more than that - and the caller's limit,
        // not the page size, is what decides how much of a backlog one sweep takes.
        for (var page = 1; keys.Count < limit; page++)
        {
            var perPage = Math.Min(SearchPageSize, limit - keys.Count);

            using var request = Authorized(HttpMethod.Post, Url($"v2/issues/_search?perPage={perPage}&page={page}"));
            request.Content = JsonContent.Create(new { query });

            var response = await http.SendAsync(request, ct).ConfigureAwait(false);
            // Thrown rather than answered with what arrived: an empty list here means "nothing is
            // open", so a 401 from a wrong token would otherwise read as a healthy, quiet sweep.
            response.EnsureSuccessStatusCode();

            var batch = await response.Content
                .ReadFromJsonAsync<List<YtIssueKeyDto>>(cancellationToken: ct).ConfigureAwait(false) ?? [];

            keys.AddRange(batch
                .Select(i => i.Key)
                .Where(k => !string.IsNullOrWhiteSpace(k))
                .Select(k => k!));

            // A short page is the last one. Without this the loop would keep asking for pages past the
            // end until it happened to fill the limit, which for a small tracker is every sweep.
            if (batch.Count < perPage) break;
        }

        return keys;
    }

    private Uri Url(string relative) => new($"{context.ApiUrl.ToString().TrimEnd('/')}/{relative}");

    private HttpRequestMessage Authorized(HttpMethod method, Uri url)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("OAuth", context.AccessToken);
        request.Headers.Add("X-Org-Id", options.OrgId);
        return request;
    }

    // Only the key: the search answers with whole issues, but every one of them is re-read through the
    // single sync path, so parsing more here would be a second, divergent reading of the same issue.
    private sealed record YtIssueKeyDto([property: JsonPropertyName("key")] string? Key);

    private sealed record YtIssueDto(
        [property: JsonPropertyName("key")] string Key,
        [property: JsonPropertyName("summary")] string? Summary,
        [property: JsonPropertyName("status")] YtStatus? Status,
        // DateTimeOffset, not DateTime: the tracker stamps an offset, and deserialising that into a
        // DateTime yields Kind=Local - which SQL Server stores without complaint and PostgreSQL
        // refuses outright, since it maps DateTime to timestamptz and Npgsql writes only Kind=Utc.
        // The ambiguity is removed here rather than compensated for later: an offset is exactly what
        // DateTimeOffset is for, and the adapter is where the tracker's format is known.
        [property: JsonPropertyName("resolvedAt")] DateTimeOffset? ResolvedAt,
        // Same shape as a link target -- an object carrying the issue key -- so it reuses YtLinkObject.
        [property: JsonPropertyName("parent")] YtLinkObject? Parent);

    private sealed record YtStatus([property: JsonPropertyName("key")] string Key);

    private sealed record YtIssueLinkDto(
        [property: JsonPropertyName("type")] YtLinkType? Type,
        [property: JsonPropertyName("direction")] string? Direction,
        [property: JsonPropertyName("object")] YtLinkObject? Object);

    private sealed record YtLinkType([property: JsonPropertyName("id")] string Id);

    private sealed record YtLinkObject([property: JsonPropertyName("key")] string Key);

    private sealed record YtTransitionDto(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("to")] YtTransitionTo? To);

    private sealed record YtTransitionTo([property: JsonPropertyName("key")] string? Key);
}
