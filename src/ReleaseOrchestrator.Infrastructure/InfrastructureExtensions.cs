using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ReleaseOrchestrator.Application.DTOs;
using ReleaseOrchestrator.Application.Services;
using ReleaseOrchestrator.Infrastructure.Actions;
using ReleaseOrchestrator.Infrastructure.Coordination;
using ReleaseOrchestrator.Infrastructure.Audit;
using ReleaseOrchestrator.Infrastructure.Archive;
using ReleaseOrchestrator.Infrastructure.Execution;
using ReleaseOrchestrator.Infrastructure.Ingestion;
using ReleaseOrchestrator.Infrastructure.Auth;
using ReleaseOrchestrator.Infrastructure.Persistence;
using ReleaseOrchestrator.Infrastructure.Providers;
using ReleaseOrchestrator.Infrastructure.Queue;
using ReleaseOrchestrator.Infrastructure.Queue.Consumers;
using ReleaseOrchestrator.Infrastructure.ReleasePlanning;
using ReleaseOrchestrator.Infrastructure.Tracker;
using ReleaseOrchestrator.Infrastructure.Vcs;
using ReleaseOrchestrator.Providers.Abstractions.Deploy;
using ReleaseOrchestrator.Providers.Abstractions.Tracker;
using ReleaseOrchestrator.Providers.Abstractions.Vcs;
using StackExchange.Redis;
using ReleaseOrchestrator.Providers.GitLab;
using ReleaseOrchestrator.Providers.YandexTracker;

namespace ReleaseOrchestrator.Infrastructure;

public static class InfrastructureExtensions
{
    private static readonly TimeSpan ExternalApiTimeout = TimeSpan.FromSeconds(30);

    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration config)
    {
        // Each sub-setup validates its own required settings synchronously, right here, rather than
        // inside a transport or provider lambda that runs lazily — a missing value has to fail the
        // deployment at startup, not at the first request when it already looked healthy.

        // Both contexts, against whichever database is configured. Connection strings and the
        // provider name are validated in there.
        services.AddDatabases(config);

        services.AddTokenProtection(config);

        services.AddSingleton(TimeProvider.System);

        services.AddScoped<TokenProtector>();
        services.AddScoped<IRolloutPlannerService, RolloutPlanner>();
        services.AddScoped<IRolloutService, RolloutService>();
        // Reads both contexts: a task's history outlives the task, so the archive is part of it.
        services.AddScoped<ITaskTimelineService, TaskTimelineService>();
        services.AddScoped<IVcsService, VcsService>();
        services.AddScoped<ITrackerService, TrackerService>();

        // The factories map a connection's provider type to an adapter. They resolve keyed
        // services, so they stay satisfiable even with no adapter registered — an unknown
        // provider type is reported by the factory, naming the ones that exist, rather than by
        // container validation.
        services.AddScoped<IVcsProviderFactory, VcsProviderFactory>();
        services.AddScoped<ITrackerProviderFactory, TrackerProviderFactory>();
        services.AddScoped<IDeployStrategyFactory, DeployStrategyFactory>();

        // The registry, in full. Adding a provider is a project plus a line here — no dynamic
        // loading, no discovery by reflection: see the note in this project's .csproj.
        services.AddGitLabProvider();
        services.AddGitLabDeployStrategies();
        services.AddYandexTrackerProvider();
        services.AddActionHandlers();

        // The permission cache and the job lease, against whichever backend is configured. Redis is
        // the default and the only one needing another service; a single-replica deployment can say
        // so and keep both in this process. Validated here, at startup, for the reason at the top of
        // this method.
        services.AddCoordination(config);

        services.Configure<PermissionBootstrapOptions>(config.GetSection("Authorization"));
        services.AddScoped<IClaimsTransformation, PermissionClaimsTransformation>();
        services.AddSingleton<IAuthorizationHandler, PermissionAuthorizationHandler>();

        services.Configure<ArchiveOptions>(config.GetSection("Archiving"));
        services.AddHostedService<ArchiveHostedService>();

        // The buffer is a singleton shared by every request; the sink is the same object seen through
        // the port, so the hot path depends on an interface that cannot be awaited.
        services.Configure<RequestAuditOptions>(config.GetSection("RequestAudit"));
        services.AddSingleton<RequestAuditBuffer>();
        services.AddSingleton<IRequestAuditSink>(sp => sp.GetRequiredService<RequestAuditBuffer>());
        // Projected out of the options so the recorder -- which lives in the web host and must not
        // reference this assembly -- can honour the privacy switches.
        services.AddSingleton<IOptions<RequestAuditPrivacy>>(sp =>
        {
            var audit = sp.GetRequiredService<IOptions<RequestAuditOptions>>().Value;
            return Options.Create(new RequestAuditPrivacy(audit.RecordClientIp, audit.RecordUserName));
        });
        services.AddHostedService<RequestAuditWriter>();
        services.AddHostedService<RequestAuditPruner>();

        services.Configure<TaskReconciliationOptions>(config.GetSection("TaskReconciliation"));
        services.AddHostedService<TaskReconciliationService>();

        services.Configure<RolloutExecutionOptions>(config.GetSection("RolloutExecution"));
        services.AddHostedService<RolloutCoordinator>();

        services.Configure<VcsPollingOptions>(config.GetSection("VcsPolling"));
        services.AddHostedService<VcsPollingCoordinator>();

        // The bus and its six handlers, over RabbitMQ. See MessagingSetup for the one-queue model
        // and the retry policy.
        services.AddMessaging(config);

        return services;
    }

    /// <summary>Names the setting and its environment form, so the fix is obvious from the log line.</summary>
    private static string Required(IConfiguration config, string key) =>
        config[key] is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException(
                $"{key} is not configured. Set {key.Replace(':', '_').Replace("_", "__")} in the environment.");
}
