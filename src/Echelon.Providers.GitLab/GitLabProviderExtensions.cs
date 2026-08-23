using Microsoft.Extensions.DependencyInjection;
using Echelon.Core.Enums;
using Echelon.Providers.Abstractions;
using Echelon.Providers.Abstractions.Deploy;
using Echelon.Providers.Abstractions.Ingestion;
using Echelon.Providers.Abstractions.Vcs;
using Echelon.Providers.GitLab.Webhooks;

namespace Echelon.Providers.GitLab;

/// <summary>Registers the GitLab adapter.</summary>
public static class GitLabProviderExtensions
{
    /// <summary>
    /// The provider type for a GitLab connection reached by webhooks. Stored on
    /// <c>VcsConnection.ProviderType</c>.
    /// </summary>
    /// <remarks>
    /// GitLab is two provider types, not one with a push/poll toggle: <see cref="WebhookProviderType"/>
    /// pushes events to the ingress and declares no extra settings, while
    /// <see cref="PollProviderType"/> is polled and declares its interval. Both share one API adapter
    /// and one set of deploy strategies - only ingestion differs.
    /// </remarks>
    public const string WebhookProviderType = "gitlab-webhook";

    /// <summary>The provider type for a GitLab connection this service polls. See <see cref="WebhookProviderType"/>.</summary>
    public const string PollProviderType = "gitlab-poll";

    private static readonly TimeSpan ApiTimeout = TimeSpan.FromSeconds(30);

    /// <summary>Adds the GitLab adapter to the container.</summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same collection, for chaining.</returns>
    /// <remarks>
    /// <para>
    /// Keyed by provider type, because the key the factory looks up arrives from the
    /// database at runtime. That rules out <c>[FromKeyedServices]</c>, which binds a key at
    /// compile time; the lookup is a <c>GetRequiredKeyedService</c> inside the factory instead.
    /// </para>
    /// <para>
    /// The matching <see cref="VcsProviderRegistration"/> is what makes the adapter discoverable
    /// rather than merely resolvable: keyed DI cannot enumerate its keys, so without it nothing
    /// could list the providers that exist - neither the factory's error message nor the API's
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

        // Two provider types over one shared connect logic: the wrappers differ only in provider type
        // and declared settings (the poll type carries an interval; the webhook type carries none).
        services.AddKeyedScoped<IVcsProviderAdapter>(
            WebhookProviderType,
            (sp, _) => new GitLabWebhookProviderAdapter(sp.GetRequiredService<GitLabProviderAdapter>()));
        services.AddKeyedScoped<IVcsProviderAdapter>(
            PollProviderType,
            (sp, _) => new GitLabPollProviderAdapter(sp.GetRequiredService<GitLabProviderAdapter>()));

        services.AddSingleton(new VcsProviderRegistration(WebhookProviderType, IngestionMode.Push,
            "GitLab (webhook). Receives merge-request events GitLab pushes to /webhooks/gitlab/{connection}."));
        services.AddSingleton(new VcsProviderRegistration(PollProviderType, IngestionMode.Poll,
            "GitLab (polling). Lists open merge requests on a timer you set; no webhook endpoint."));

        return services;
    }

    /// <summary>The deploy strategy key that merges the merge request.</summary>
    public const string MergeStrategyKey = "gitlab-merge";

    /// <summary>The deploy strategy key that triggers a pipeline.</summary>
    public const string PipelineStrategyKey = "gitlab-pipeline";

    /// <summary>
    /// The deploy strategy key that runs one named job of the merge request's own latest pipeline.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="PipelineStrategyKey"/>, which starts a new pipeline from a ref: this
    /// one presses a button on the pipeline the merge request already has, so the deploy runs against
    /// what was tested rather than against the branch head.
    /// </remarks>
    public const string JobStrategyKey = "gitlab-job";

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
        services.AddSingleton(new DeployStrategyRegistration(MergeStrategyKey, "Deploys by merging the merge request."));

        services.AddHttpClient<GitLabPipelineStrategy>(c => c.Timeout = ApiTimeout);
        services.AddKeyedScoped<IDeployStrategy>(PipelineStrategyKey, (sp, _) => sp.GetRequiredService<GitLabPipelineStrategy>());
        services.AddSingleton(new DeployStrategyRegistration(PipelineStrategyKey, "Deploys by triggering a pipeline."));

        services.AddHttpClient<GitLabJobStrategy>(c => c.Timeout = ApiTimeout);
        services.AddKeyedScoped<IDeployStrategy>(JobStrategyKey, (sp, _) => sp.GetRequiredService<GitLabJobStrategy>());
        services.AddSingleton(new DeployStrategyRegistration(JobStrategyKey,
            "Deploys by running one job of the merge request's latest pipeline."));

        return services;
    }

    /// <summary>
    /// Adds the GitLab webhook parser, keyed by <see cref="WebhookProviderType"/> and paired with a
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

        // Only the webhook type has a webhook: the poll type receives no pushes, so no parser and no
        // route are mounted for it.
        services.AddKeyedSingleton<IWebhookParser, GitLabWebhookParser>(WebhookProviderType);
        services.AddSingleton(new WebhookParserRegistration(WebhookProviderType));

        return services;
    }
}
