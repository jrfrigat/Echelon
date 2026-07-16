using MassTransit;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ReleaseOrchestrator.Application.Services;
using ReleaseOrchestrator.Infrastructure.Archive;
using ReleaseOrchestrator.Infrastructure.Auth;
using ReleaseOrchestrator.Infrastructure.Persistence;
using ReleaseOrchestrator.Infrastructure.Providers;
using ReleaseOrchestrator.Infrastructure.Queue;
using ReleaseOrchestrator.Infrastructure.Queue.Consumers;
using ReleaseOrchestrator.Infrastructure.ReleasePlanning;
using ReleaseOrchestrator.Infrastructure.Tracker;
using ReleaseOrchestrator.Infrastructure.Vcs;
using ReleaseOrchestrator.Providers.Abstractions.Tracker;
using ReleaseOrchestrator.Providers.Abstractions.Vcs;
using ReleaseOrchestrator.Providers.GitLab;
using ReleaseOrchestrator.Providers.YandexTracker;

namespace ReleaseOrchestrator.Infrastructure;

public static class InfrastructureExtensions
{
    private static readonly TimeSpan ExternalApiTimeout = TimeSpan.FromSeconds(30);

    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration config)
    {
        // Read every required setting here, before anything is registered. Checking inside the
        // Redis or RabbitMQ configuration lambdas looks like fail-fast but is not: those run
        // lazily, so a missing value surfaced at the first request or when the bus started,
        // long after the deployment looked healthy.
        var connectionString = Required(config, "ConnectionStrings:Default");
        var archiveConnectionString = Required(config, "ConnectionStrings:Archive");
        var redisConnectionString = Required(config, "Redis:ConnectionString");
        var queueUsername = Required(config, "Queue:Username");
        var queuePassword = Required(config, "Queue:Password");

        // EnableRetryOnFailure: transient faults (failover, deadlock victim, pool timeout) are
        // routine for SQL Server in containers, and without an execution strategy each one
        // surfaces as a consumer exception and burns the message's retry budget.
        services.AddDbContext<AppDbContext>(opt =>
            opt.UseSqlServer(connectionString, sql =>
            {
                sql.MigrationsAssembly("ReleaseOrchestrator.Migrations.MsSql");
                sql.EnableRetryOnFailure(3, TimeSpan.FromSeconds(5), null);
            }));

        services.AddDbContext<ArchiveDbContext>(opt =>
            opt.UseSqlServer(archiveConnectionString, sql =>
                sql.EnableRetryOnFailure(3, TimeSpan.FromSeconds(5), null)));

        services.AddDataProtection()
            .SetApplicationName("ReleaseOrchestrator")
            .PersistKeysToDbContext<AppDbContext>();

        services.AddSingleton(TimeProvider.System);

        services.AddScoped<TokenProtector>();
        services.AddScoped<IReleasePlannerService, ReleasePlanner>();
        services.AddScoped<IVcsService, VcsService>();
        services.AddScoped<ITrackerService, TrackerService>();

        // The factories map a connection's provider type to an adapter. They resolve keyed
        // services, so they stay satisfiable even with no adapter registered — an unknown
        // provider type is reported by the factory, naming the ones that exist, rather than by
        // container validation.
        services.AddScoped<IVcsProviderFactory, VcsProviderFactory>();
        services.AddScoped<ITrackerProviderFactory, TrackerProviderFactory>();

        // The registry, in full. Adding a provider is a project plus a line here — no dynamic
        // loading, no discovery by reflection: see the note in this project's .csproj.
        services.AddGitLabProvider();
        services.AddYandexTrackerProvider();

        services.AddStackExchangeRedisCache(opt => opt.Configuration = redisConnectionString);

        services.Configure<PermissionBootstrapOptions>(config.GetSection("Authorization"));
        services.AddScoped<IClaimsTransformation, PermissionClaimsTransformation>();
        services.AddSingleton<IAuthorizationHandler, PermissionAuthorizationHandler>();

        services.Configure<ArchiveOptions>(config.GetSection("Archiving"));
        services.AddHostedService<ArchiveHostedService>();

        services.Configure<TaskReconciliationOptions>(config.GetSection("TaskReconciliation"));
        services.AddHostedService<TaskReconciliationService>();

        services.AddMassTransit(x =>
        {
            x.AddConsumer<MrOpenedConsumer>();
            x.AddConsumer<MrStatusChangedConsumer>();
            x.AddConsumer<TaskCreatedConsumer>();
            x.AddConsumer<TaskStatusChangedConsumer>();
            x.AddConsumer<TaskSyncConsumer>();
            x.AddConsumer<ReleasePlanRecalculationConsumer>();

            x.UsingRabbitMq((ctx, cfg) =>
            {
                cfg.Host(config["Queue:Host"] ?? "localhost", h =>
                {
                    h.Username(queueUsername);
                    h.Password(queuePassword);
                });

                // Immediate retries cover the brief window where a status event overtakes its
                // opened event; scheduled redelivery covers longer outages of GitLab/Tracker.
                cfg.UseMessageRetry(r => r.Exponential(5, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(5)));
                cfg.UseDelayedRedelivery(r => r.Intervals(TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15)));

                // Stops a failing dependency from being hammered by the whole consumer pool.
                cfg.UseKillSwitch(k => k
                    .SetActivationThreshold(10)
                    .SetTripThreshold(0.5)
                    .SetRestartTimeout(TimeSpan.FromMinutes(1)));

                cfg.PrefetchCount = config.GetValue("Queue:PrefetchCount", 16);

                cfg.ConfigureEndpoints(ctx);
            });
        });

        return services;
    }

    /// <summary>Names the setting and its environment form, so the fix is obvious from the log line.</summary>
    private static string Required(IConfiguration config, string key) =>
        config[key] is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException(
                $"{key} is not configured. Set {key.Replace(':', '_').Replace("_", "__")} in the environment.");
}
