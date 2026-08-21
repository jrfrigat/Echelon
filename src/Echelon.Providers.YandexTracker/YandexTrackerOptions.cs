using Echelon.Providers.Abstractions.Tracker;

namespace Echelon.Providers.YandexTracker;

/// <summary>
/// The settings a Yandex.Tracker connection needs, typed.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="OrgId"/> is the reason this type exists. It used to sit in the shared tracker port
/// - <c>GetIssueAsync(apiUrl, orgId, token, issueKey, ct)</c> - and on the shared entity as a
/// column. An organization id is a Yandex.Tracker concept; Jira has no equivalent. A contract
/// that names it does not describe "a tracker", it describes this one, and every other adapter
/// would have had to accept a parameter it has no use for.
/// </para>
/// <para>
/// The settings arrive as an opaque bag on <see cref="TrackerProviderContext.ProviderSettings"/>
/// and are given meaning here, by the only code that knows what they mean.
/// </para>
/// </remarks>
/// <param name="OrgId">The organization the token belongs to; sent as the <c>X-Org-Id</c> header.</param>
/// <param name="ClosedStatuses">The status keys this connection treats as "done", case-insensitive.</param>
/// <param name="Queues">The queue keys a poll searches for open issues; empty when none were named.</param>
/// <param name="SearchQuery">A whole query in the tracker's own language, overriding <paramref name="Queues"/>; null when unset.</param>
public sealed record YandexTrackerOptions(
    string OrgId,
    IReadOnlySet<string> ClosedStatuses,
    IReadOnlyList<string> Queues,
    string? SearchQuery)
{
    /// <summary>The settings key that carries the organization id.</summary>
    public const string OrgIdKey = "orgId";

    /// <summary>The settings key that carries the comma-separated closed-status keys.</summary>
    public const string ClosedStatusesKey = "closedStatuses";

    /// <summary>The settings key that carries the comma-separated queue keys a poll searches.</summary>
    public const string QueuesKey = "queues";

    /// <summary>The settings key that carries a hand-written search query, used as-is.</summary>
    public const string SearchQueryKey = "searchQuery";

    /// <summary>Reads the options out of a connection's settings bag.</summary>
    /// <param name="context">The connection being bound.</param>
    /// <returns>The typed options.</returns>
    /// <exception cref="InvalidOperationException">
    /// The connection has no organization id. Thrown rather than defaulted to empty: the previous
    /// code passed <c>OrgId ?? string.Empty</c>, which sent a blank <c>X-Org-Id</c> and turned a
    /// missing setting into an opaque 401 from the tracker instead of a sentence naming the fix.
    /// </exception>
    public static YandexTrackerOptions From(TrackerProviderContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        context.ProviderSettings.TryGetValue(OrgIdKey, out var orgId);

        if (string.IsNullOrWhiteSpace(orgId))
            throw new InvalidOperationException(
                $"Tracker connection '{context.ConnectionName}' is served by the Yandex.Tracker provider, "
                + $"which requires an organization id. Set '{OrgIdKey}' in the connection's provider settings.");

        // Which statuses mean "done" is a per-project decision (a workflow may call it "done" or
        // "deployed"), so it is configurable; an unset value keeps the default set.
        context.ProviderSettings.TryGetValue(ClosedStatusesKey, out var closed);
        var closedStatuses = string.IsNullOrWhiteSpace(closed)
            ? new HashSet<string>(YandexTrackerStatusRules.DefaultClosedStatuses, StringComparer.OrdinalIgnoreCase)
            : closed.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Searching is only meaningful for a polled connection, so neither key is validated here: a
        // webhook connection is missing both and must still connect. The search itself says what is
        // missing, at the moment it is actually needed.
        context.ProviderSettings.TryGetValue(QueuesKey, out var queues);
        context.ProviderSettings.TryGetValue(SearchQueryKey, out var query);

        return new YandexTrackerOptions(
            orgId.Trim(),
            closedStatuses,
            queues?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? [],
            string.IsNullOrWhiteSpace(query) ? null : query.Trim());
    }

    /// <summary>Builds the query that lists this connection's open issues.</summary>
    /// <param name="connectionName">Named in the error, so an operator knows which connection to fix.</param>
    /// <returns>A query in Yandex.Tracker's query language.</returns>
    /// <exception cref="InvalidOperationException">Neither a queue nor a query was configured.</exception>
    /// <remarks>
    /// The hand-written query wins outright: a workflow that calls its open states something else, or a
    /// filter by component or assignee, cannot be expressed by a queue list, and an operator who has
    /// written the query means it. Otherwise the queues become the obvious query - everything in them
    /// that has no resolution.
    /// </remarks>
    public string BuildSearchQuery(string connectionName)
    {
        if (SearchQuery is { Length: > 0 } query)
            return query;

        if (Queues.Count == 0)
            throw new InvalidOperationException(
                $"Tracker connection '{connectionName}' is polled, so it has to say what to look in. "
                + $"Set '{QueuesKey}' to the queue keys to sweep, or '{SearchQueryKey}' to a query of your own.");

        return $"Queue: {string.Join(", ", Queues)} AND Resolution: empty()";
    }
}
