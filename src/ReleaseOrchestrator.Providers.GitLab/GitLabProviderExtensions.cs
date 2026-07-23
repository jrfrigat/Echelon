using Microsoft.Extensions.DependencyInjection;
using ReleaseOrchestrator.Providers.Abstractions;
using ReleaseOrchestrator.Providers.Abstractions.Deploy;
using ReleaseOrchestrator.Providers.Abstractions.Ingestion;
using ReleaseOrchestrator.Providers.Abstractions.Vcs;
using ReleaseOrchestrator.Providers.GitLab.Webhooks;

namespace ReleaseOrchestrator.Providers.GitLab;

/// <summary>Registers the GitLab adapter.</summary>
public static class GitLabProviderExtensions
{
    /// <summary>
    /// The provider type this adapter serves. Stored on <c>VcsConnection.ProviderType</c>.
    /// </summary>
    public const string ProviderType = "gitlab";

    private static readonly TimeSpan ApiTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Adds the GitLab adapter to the container.</summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same collection, for chaining.</returns>
    /// <remarks>
    /// <para>
    /// Keyed by <see cref="ProviderType"/>, because the key the factory looks up arrives from the
    /// database at runtime. That rules out <c>[FromKeyedServices]</c>, which binds a key at
    /// compile time; the lookup is a <c>GetRequiredKeyedService</c> inside the factory instead.
    /// </para>
    /// <para>
    /// The matching <see cref="VcsProviderRegistration"/> is what makes the adapter discoverable
    /// rather than merely resolvable: keyed DI cannot enumerate its keys, so without it nothing
    /// could list the providers that exist — neither the factory's error message nor the API's
    /// validation of an operator's input.
    /// </para>
    /// </remarks>
    public static IServiceCollection AddGitLabProvider(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<GitLabVersionDetector>();

        // Typed client on the adapter: the timeout is then actually applied. Binding it to a
        // concrete type nobody resolves is how these registrations went dead before.
        services.AddHttpClient<GitLabProviderAdapter>(c => c.Timeout = ApiTimeout);

        services.AddKeyedScoped<IVcsProviderAdapter>(
            ProviderType,
            (sp, _) => sp.GetRequiredService<GitLabProviderAdapter>());

        services.AddSingleton(new VcsProviderRegistration(ProviderType));

        return services;
    }

    /// <summary>The deploy strategy key that merges the merge request.</summary>
    public const string MergeStrategyKey = "gitlab-merge";

    /// <summary>The deploy strategy key that triggers a pipeline.</summary>
    public const string PipelineStrategyKey = "gitlab-pipeline";

    /// <summary>
    /// Adds the GitLab deploy strategies (merge and pipeline). Each is keyed and paired with a
    /// <see cref="DeployStrategyRegistration"/> so the factory can enumerate and validate the keys,
    /// exactly as the read provider is registered.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same collection, for chaining.</returns>
    public static IServiceCollection AddGitLabDeployStrategies(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddHttpClient<GitLabMergeStrategy>(c => c.Timeout = ApiTimeout);
        services.AddKeyedScoped<IDeployStrategy>(MergeStrategyKey, (sp, _) => sp.GetRequiredService<GitLabMergeStrategy>());
        services.AddSingleton(new DeployStrategyRegistration(MergeStrategyKey));

        services.AddHttpClient<GitLabPipelineStrategy>(c => c.Timeout = ApiTimeout);
        services.AddKeyedScoped<IDeployStrategy>(PipelineStrategyKey, (sp, _) => sp.GetRequiredService<GitLabPipelineStrategy>());
        services.AddSingleton(new DeployStrategyRegistration(PipelineStrategyKey));

        return services;
    }

    /// <summary>
    /// Adds the GitLab webhook parser, keyed by <see cref="ProviderType"/> and paired with a
    /// <see cref="WebhookParserRegistration"/> so the host can enumerate the parsers and mount one
    /// route per provider.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same collection, for chaining.</returns>
    /// <remarks>
    /// Separate from <see cref="AddGitLabProvider"/> because the ingress host wants the webhook
    /// parser but not the HTTP read-adapter (it never calls the GitLab API), and the API host wants
    /// the reverse. Registering them together would drag one host's unused dependencies into the
    /// other.
    /// </remarks>
    public static IServiceCollection AddGitLabWebhookParser(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddKeyedSingleton<IWebhookParser, GitLabWebhookParser>(ProviderType);
        services.AddSingleton(new WebhookParserRegistration(ProviderType));

        return services;
    }
}
