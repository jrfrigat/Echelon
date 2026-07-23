using Microsoft.Extensions.DependencyInjection;
using ReleaseOrchestrator.Providers.Abstractions;
using ReleaseOrchestrator.Providers.Abstractions.Ingestion;
using ReleaseOrchestrator.Providers.Abstractions.Tracker;
using ReleaseOrchestrator.Providers.YandexTracker.Webhooks;

namespace ReleaseOrchestrator.Providers.YandexTracker;

/// <summary>Registers the Yandex.Tracker adapter.</summary>
public static class YandexTrackerProviderExtensions
{
    /// <summary>
    /// The provider type this adapter serves. Stored on <c>TrackerConnection.ProviderType</c>.
    /// </summary>
    public const string ProviderType = "yandextracker";

    private static readonly TimeSpan ApiTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Adds the Yandex.Tracker adapter to the container.</summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same collection, for chaining.</returns>
    /// <remarks>See <c>GitLabProviderExtensions.AddGitLabProvider</c> for why the key is keyed and registered twice.</remarks>
    public static IServiceCollection AddYandexTrackerProvider(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddHttpClient<YandexTrackerProviderAdapter>(c => c.Timeout = ApiTimeout);

        services.AddKeyedScoped<ITrackerProviderAdapter>(
            ProviderType,
            (sp, _) => sp.GetRequiredService<YandexTrackerProviderAdapter>());

        services.AddSingleton(new TrackerProviderRegistration(ProviderType));

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

        services.AddKeyedSingleton<IWebhookParser, YandexTrackerWebhookParser>(ProviderType);
        services.AddSingleton(new WebhookParserRegistration(ProviderType));

        return services;
    }
}
