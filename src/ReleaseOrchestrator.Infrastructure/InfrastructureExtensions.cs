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
using ReleaseOrchestrator.Infrastructure.Queue.Consumers;
using ReleaseOrchestrator.Infrastructure.ReleasePlanning;
using ReleaseOrchestrator.Infrastructure.Tracker;
using ReleaseOrchestrator.Infrastructure.Vcs;

namespace ReleaseOrchestrator.Infrastructure;

public static class InfrastructureExtensions
{
    private static readonly TimeSpan ExternalApiTimeout = TimeSpan.FromSeconds(30);

    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration config)
    {
        var connectionString = config.GetConnectionString("Default")
            ?? throw new InvalidOperationException(
                "ConnectionStrings:Default is not configured. Set ConnectionStrings__Default in the environment.");
        var archiveConnectionString = config.GetConnectionString("Archive")
            ?? throw new InvalidOperationException(
                "ConnectionStrings:Archive is not configured. Set ConnectionStrings__Archive in the environment.");

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

        // Registered against the interface: consumers only ever resolve the interface, so
        // binding the typed client to the concrete type left these registrations dead and
        // any timeout or retry policy configured on them silently unapplied.
        services.AddHttpClient<IVcsApiClient, GitLabApiClient>(c => c.Timeout = ExternalApiTimeout);
        services.AddHttpClient<ITrackerApiClient, YandexTrackerApiClient>(c => c.Timeout = ExternalApiTimeout);

        services.AddStackExchangeRedisCache(opt =>
            opt.Configuration = config["Redis:ConnectionString"]
                ?? throw new InvalidOperationException(
                    "Redis:ConnectionString is not configured. Set Redis__ConnectionString in the environment."));

        services.AddScoped<IClaimsTransformation, PermissionClaimsTransformation>();
        services.AddSingleton<IAuthorizationHandler, PermissionAuthorizationHandler>();

        services.Configure<ArchiveOptions>(config.GetSection("Archiving"));
        services.AddHostedService<ArchiveHostedService>();

        services.AddMassTransit(x =>
        {
            x.AddConsumer<MrOpenedConsumer>();
            x.AddConsumer<MrStatusChangedConsumer>();
            x.AddConsumer<TaskCreatedConsumer>();
            x.AddConsumer<TaskStatusChangedConsumer>();
            x.AddConsumer<ReleasePlanRecalculationConsumer>();

            x.UsingRabbitMq((ctx, cfg) =>
            {
                cfg.Host(config["Queue:Host"] ?? "localhost", h =>
                {
                    h.Username(config["Queue:Username"]
                        ?? throw new InvalidOperationException("Queue:Username is not configured."));
                    h.Password(config["Queue:Password"]
                        ?? throw new InvalidOperationException("Queue:Password is not configured."));
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
}
