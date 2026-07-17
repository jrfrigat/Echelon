using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ReleaseOrchestrator.Application.Services;
using ReleaseOrchestrator.Infrastructure.Queue;
using ReleaseOrchestrator.Providers.Abstractions;
using StackExchange.Redis;

namespace ReleaseOrchestrator.Infrastructure.Coordination;

/// <summary>
/// Registers the permission cache and the job lease against the configured backend.
/// </summary>
/// <remarks>
/// Selected at startup rather than resolved per call, so unlike the VCS and tracker registries
/// there are no keyed services here: the choice comes from configuration and cannot change while
/// the process runs. What is borrowed from those registries is the part that matters to an
/// operator — an unrecognised name fails at startup and the message lists what is registered,
/// rather than surfacing as a missing service at the first request.
/// </remarks>
public static class CoordinationSetup
{
    /// <summary>Registers <see cref="IDistributedLease"/> and the distributed cache.</summary>
    /// <param name="services">The service collection.</param>
    /// <param name="config">Application configuration.</param>
    /// <returns>The same collection, for chaining.</returns>
    /// <exception cref="UnknownProviderException">The named provider is not registered.</exception>
    /// <exception cref="InvalidOperationException">The provider's own settings are missing or contradictory.</exception>
    public static IServiceCollection AddCoordination(this IServiceCollection services, IConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(config);

        var section = config.GetSection(CoordinationOptions.SectionName);
        var options = section.Get<CoordinationOptions>() ?? new CoordinationOptions();
        var provider = ProviderKey.Normalize(options.Provider);

        if (!CoordinationProviders.All.Contains(provider, StringComparer.Ordinal))
            throw new UnknownProviderException("coordination", options.Provider, CoordinationProviders.All);

        services.Configure<CoordinationOptions>(section);

        return provider switch
        {
            CoordinationProviders.Memory => AddInProcess(services, options),
            CoordinationProviders.Redis => AddRedis(services, config),
            _ => throw new UnknownProviderException("coordination", options.Provider, CoordinationProviders.All)
        };
    }

    private static IServiceCollection AddInProcess(IServiceCollection services, CoordinationOptions options)
    {
        // The one thing this build cannot check for itself. An in-process lease under two replicas
        // is not degraded, it is wrong — both lead — so the operator states the fact that makes it
        // correct, and states it about the deployment rather than about Redis.
        if (!options.SingleInstance)
            throw new InvalidOperationException(
                $"{CoordinationOptions.SectionName}:Provider is '{CoordinationProviders.Memory}', which keeps the "
                + "permission cache and the job lease inside this process. That is correct for exactly one replica "
                + "and wrong for more than one: every replica would hold its own lease and run its own archive "
                + $"cycle. Set {CoordinationOptions.SectionName}:SingleInstance=true to confirm this deployment "
                + $"runs a single replica, or use Provider='{CoordinationProviders.Redis}'.");

        services.AddDistributedMemoryCache();
        services.AddSingleton<IDistributedLease, InProcessLease>();
        return services;
    }

    private static IServiceCollection AddRedis(IServiceCollection services, IConfiguration config)
    {
        var connectionString = config["Redis:ConnectionString"];
        if (string.IsNullOrEmpty(connectionString))
            throw new InvalidOperationException(
                $"{CoordinationOptions.SectionName}:Provider is '{CoordinationProviders.Redis}' but "
                + "Redis:ConnectionString is not configured. Set Redis__ConnectionString in the environment, or "
                + $"use Provider='{CoordinationProviders.Memory}' with {CoordinationOptions.SectionName}"
                + "__SingleInstance=true for a single-replica deployment.");

        services.AddStackExchangeRedisCache(opt => opt.Configuration = connectionString);

        // A multiplexer of our own: AddStackExchangeRedisCache keeps its connection private, and
        // IDistributedCache has no compare-and-set — which is the whole of a lease.
        // Connect lazily so a Redis that is down delays the first lease, not startup.
        services.AddSingleton<IConnectionMultiplexer>(_ =>
            ConnectionMultiplexer.Connect(ConfigurationOptions.Parse(connectionString, ignoreUnknown: true)));
        services.AddSingleton<IDistributedLease, RedisDistributedLease>();
        return services;
    }
}
