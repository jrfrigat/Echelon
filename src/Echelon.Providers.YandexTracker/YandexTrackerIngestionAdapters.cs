using Echelon.Providers.Abstractions;
using Echelon.Providers.Abstractions.Tracker;
using Echelon.Providers.Abstractions.Vcs;

namespace Echelon.Providers.YandexTracker;

/// <summary>
/// Yandex.Tracker reached by webhooks: the tracker pushes task events to the ingress, and this service
/// only calls the API to read an issue and to write status/comments.
/// </summary>
/// <remarks>
/// A thin wrapper over the shared <see cref="YandexTrackerProviderAdapter"/>: the API behaviour is
/// identical to the poll type, so only the provider type and the declared settings differ. This type
/// declares no extra settings beyond the base (org id, closed statuses) - the webhook shared secret is
/// configuration of the ingress host, not of the connection.
/// </remarks>
internal sealed class YandexTrackerWebhookAdapter(YandexTrackerProviderAdapter inner) : ITrackerProviderAdapter
{
    /// <inheritdoc/>
    public IReadOnlyList<ProviderSettingSchema> SettingsSchema { get; } = [.. inner.SettingsSchema];

    /// <inheritdoc/>
    public Task<ITrackerProvider> ConnectAsync(TrackerProviderContext context, CancellationToken ct) =>
        inner.ConnectAsync(context, ct);
}

/// <summary>
/// Yandex.Tracker with no webhook: the service re-reads this connection's open tasks on an interval,
/// for a deployment the tracker cannot push to.
/// </summary>
/// <remarks>
/// The sibling of <see cref="YandexTrackerWebhookAdapter"/> over the same shared connect logic. It
/// declares the one setting polling needs - the interval - under the neutral
/// <see cref="VcsPollSettings.IntervalKey"/> (a poll interval is not a VCS concept, only spelled there),
/// so the tracker poller reads it without knowing this is Yandex.Tracker. Dependency links still stay
/// fresh through the reconciliation pass regardless of type; this only adds a faster status cadence.
/// </remarks>
internal sealed class YandexTrackerPollAdapter(YandexTrackerProviderAdapter inner) : ITrackerProviderAdapter
{
    /// <inheritdoc/>
    public IReadOnlyList<ProviderSettingSchema> SettingsSchema { get; } =
    [
        new(VcsPollSettings.IntervalKey,
            Label: "Poll interval (seconds)",
            Description: "How often to re-read this connection's open tasks.",
            Kind: ProviderSettingKind.Int,
            Default: VcsPollSettings.DefaultIntervalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Min: VcsPollSettings.MinIntervalSeconds,
            Max: VcsPollSettings.MaxIntervalSeconds),
        // Neither key is Required: a poll needs one of the two, and a schema cannot say "either". The
        // sweep names the missing one when it runs (YandexTrackerOptions.BuildSearchQuery), and the
        // poll endpoint reports that sentence back to the operator rather than discovering nothing.
        new(YandexTrackerOptions.QueuesKey,
            Label: "Queues to sweep",
            Description: "Comma-separated queue keys. A poll looks for issues with no resolution in these, "
                + "which is how tasks are discovered before anything links to them.",
            Kind: ProviderSettingKind.Text),
        new(YandexTrackerOptions.SearchQueryKey,
            Label: "Search query (optional)",
            Description: "A whole query in Yandex.Tracker's language, used instead of the queues above - "
                + "for a workflow whose open states a resolution filter does not describe.",
            Kind: ProviderSettingKind.Text),
        .. inner.SettingsSchema
    ];

    /// <inheritdoc/>
    public Task<ITrackerProvider> ConnectAsync(TrackerProviderContext context, CancellationToken ct) =>
        inner.ConnectAsync(context, ct);
}
