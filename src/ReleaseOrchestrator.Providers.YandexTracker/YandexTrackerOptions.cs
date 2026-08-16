using ReleaseOrchestrator.Providers.Abstractions.Tracker;

namespace ReleaseOrchestrator.Providers.YandexTracker;

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
public sealed record YandexTrackerOptions(string OrgId, IReadOnlySet<string> ClosedStatuses)
{
    /// <summary>The settings key that carries the organization id.</summary>
    public const string OrgIdKey = "orgId";

    /// <summary>The settings key that carries the comma-separated closed-status keys.</summary>
    public const string ClosedStatusesKey = "closedStatuses";

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

        return new YandexTrackerOptions(orgId.Trim(), closedStatuses);
    }
}
