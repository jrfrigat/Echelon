using Microsoft.Extensions.DependencyInjection;
using Echelon.Core.Enums;
using Echelon.Providers.Abstractions;
using Echelon.Providers.Abstractions.Ingestion;
using Echelon.Providers.Abstractions.Tracker;
using Echelon.Providers.YandexTracker.Webhooks;

namespace Echelon.Providers.YandexTracker;

/// <summary>Registers the Yandex.Tracker adapter.</summary>
public static class YandexTrackerProviderExtensions
{
    /// <summary>The base name this adapter serves; the two concrete types below are what a connection stores.</summary>
    public const string ProviderType = "yandextracker";

    /// <summary>
    /// The provider type for a Yandex.Tracker connection reached by webhooks. Stored on
    /// <c>TrackerConnection.ProviderType</c>.
    /// </summary>
    /// <remarks>
    /// Yandex.Tracker is two provider types, not one with a push/poll toggle: <see cref="WebhookProviderType"/>
    /// receives task webhooks (and is reconciled), while <see cref="PollProviderType"/> receives no webhook
    /// and is only re-read by the reconciliation pass. Both share one API read adapter and one settings
    /// schema - only ingestion differs.
    /// </remarks>
    public const string WebhookProviderType = "yandextracker-webhook";

    /// <summary>The provider type for a Yandex.Tracker connection with no webhook. See <see cref="WebhookProviderType"/>.</summary>
    public const string PollProviderType = "yandextracker-poll";

    private static readonly TimeSpan ApiTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Adds the Yandex.Tracker adapter to the container.</summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same collection, for chaining.</returns>
    /// <remarks>See <c>GitLabProviderExtensions.AddGitLabProvider</c> for why the key is keyed and registered twice.</remarks>
    public static IServiceCollection AddYandexTrackerProvider(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddHttpClient<YandexTrackerProviderAdapter>(c => c.Timeout = ApiTimeout);

        // Both types share one read adapter; thin wrappers give each its own settings schema (the poll
        // type adds an interval) and description, and only the webhook type gets a webhook (below).
        services.AddKeyedScoped<ITrackerProviderAdapter>(
            WebhookProviderType, (sp, _) => new YandexTrackerWebhookAdapter(sp.GetRequiredService<YandexTrackerProviderAdapter>()));
        services.AddKeyedScoped<ITrackerProviderAdapter>(
            PollProviderType, (sp, _) => new YandexTrackerPollAdapter(sp.GetRequiredService<YandexTrackerProviderAdapter>()));

        services.AddSingleton(new TrackerProviderRegistration(WebhookProviderType, IngestionMode.Push,
            "Yandex.Tracker (webhook). Reads issues and statuses; receives task webhooks at /webhooks/tracker/{connection}."));
        services.AddSingleton(new TrackerProviderRegistration(PollProviderType, IngestionMode.Poll,
            "Yandex.Tracker (polling). No webhook; open tasks are re-read on the interval you set."));

        return services;
    }

    /// <summary>
    /// Adds the Yandex.Tracker webhook parser, keyed by <see cref="ProviderType"/> and paired with a
    /// <see cref="WebhookParserRegistration"/> so the host can enumerate the parsers.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same collection, for chaining.</returns>
    /// <remarks>Separate from the read-adapter for the same reason as <c>GitLabProviderExtensions.AddGitLabWebhookParser</c>.</remarks>
    public static IServiceCollection AddYandexTrackerWebhookParser(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Only the webhook type has a webhook. The route (/webhooks/tracker/{connection}) and dedup
        // source prefix are unchanged (see the parser's descriptor), so this re-key breaks no live webhook.
        services.AddKeyedSingleton<IWebhookParser, YandexTrackerWebhookParser>(WebhookProviderType);
        services.AddSingleton(new WebhookParserRegistration(WebhookProviderType));

        return services;
    }
}
