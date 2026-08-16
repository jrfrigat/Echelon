using Echelon.Providers.Abstractions;
using Echelon.Providers.Abstractions.Tracker;

namespace Echelon.Providers.YandexTracker;

/// <summary>
/// Builds an <see cref="ITrackerProvider"/> for a Yandex.Tracker connection.
/// </summary>
/// <remarks>
/// No version detection: Yandex.Tracker is a hosted service with one version, so there is nothing
/// to ask. The GitLab adapter does detect, because a self-hosted install can be any version - the
/// mechanism is per adapter precisely so each pays only for what it needs.
/// </remarks>
internal sealed class YandexTrackerProviderAdapter(HttpClient http) : ITrackerProviderAdapter
{
    /// <inheritdoc/>
    public IReadOnlyList<ProviderSettingSchema> SettingsSchema { get; } =
    [
        new(YandexTrackerOptions.OrgIdKey,
            Label: "Organization ID",
            Description: "Yandex.Tracker organization the token belongs to; sent as the X-Org-Id header.",
            Required: true),
        new(YandexTrackerOptions.ClosedStatusesKey,
            Label: "Closed statuses",
            Description: "Comma-separated status keys that mean a task is done. Leave blank for the defaults.",
            Kind: ProviderSettingKind.Text,
            Default: string.Join(", ", YandexTrackerStatusRules.DefaultClosedStatuses))
    ];

    /// <inheritdoc/>
    public Task<ITrackerProvider> ConnectAsync(TrackerProviderContext context, CancellationToken ct)
    {
        // Validated at connect, not at first call: a connection missing its organization id is a
        // configuration error, and it should be named as one the moment the connection is used.
        var options = YandexTrackerOptions.From(context);

        return Task.FromResult<ITrackerProvider>(new YandexTrackerProvider(http, context, options));
    }
}
