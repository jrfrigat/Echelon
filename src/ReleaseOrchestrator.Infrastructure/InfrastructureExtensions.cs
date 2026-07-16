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
using ReleaseOrchestrator.Infrastructure.Queue;
using ReleaseOrchestrator.Infrastructure.Queue.Consumers;
using ReleaseOrchestrator.Infrastructure.ReleasePlanning;
using ReleaseOrchestrator.Infrastructure.Tracker;
using ReleaseOrchestrator.Infrastructure.Vcs;

namespace ReleaseOrchestrator.Infrastructure;

public static class InfrastructureExtensions
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration config)
    {
        var provider = config["DatabaseProvider"] ?? "SqlServer";

        services.AddDbContext<AppDbContext>(opt =>
        {
            if (provider == "SqlServer")
                opt.UseSqlServer(config.GetConnectionString("Default"), sql => sql.MigrationsAssembly("ReleaseOrchestrator.Migrations.MsSql"));
            else
                opt.UseNpgsql(config.GetConnectionString("Default"), npg => npg.MigrationsAssembly("ReleaseOrchestrator.Migrations.PostgreSql"));
        });

        services.AddDbContext<ArchiveDbContext>(opt =>
        {
            if (provider == "SqlServer")
                opt.UseSqlServer(config.GetConnectionString("Archive"));
            else
                opt.UseNpgsql(config.GetConnectionString("Archive"));
        });

        services.AddDataProtection()
            .SetApplicationName("ReleaseOrchestrator")
            .PersistKeysToDbContext<AppDbContext>();

        services.AddScoped<TokenProtector>();
        services.AddScoped<IReleasePlannerService, ReleasePlanner>();
        services.AddScoped<IVcsService, VcsService>();
        services.AddScoped<ITrackerService, TrackerService>();
        services.AddScoped<IVcsApiClient, GitLabApiClient>();
        services.AddScoped<ITrackerApiClient, YandexTrackerApiClient>();

        services.AddHttpClient<GitLabApiClient>();
        services.AddHttpClient<YandexTrackerApiClient>();

        services.AddStackExchangeRedisCache(opt => opt.Configuration = config["Redis:ConnectionString"] ?? "localhost:6379");

        services.AddScoped<IClaimsTransformation, PermissionClaimsTransformation>();
        services.AddSingleton<IAuthorizationHandler, PermissionAuthorizationHandler>();

        services.AddSingleton<DebouncedRecalculationService>();
        services.AddHostedService(sp => sp.GetRequiredService<DebouncedRecalculationService>());

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
                    h.Username(config["Queue:Username"] ?? "guest");
                    h.Password(config["Queue:Password"] ?? "guest");
                });

                cfg.UseMessageRetry(r => r.Exponential(5, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(5)));
                cfg.ConfigureEndpoints(ctx);
            });
        });

        return services;
    }
}
