using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Echelon.Infrastructure.Archive;
using Echelon.Providers.Abstractions;

namespace Echelon.Infrastructure.Persistence;

/// <summary>Registers both contexts against the configured database provider.</summary>
public static class DatabaseSetup
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(5);
    private const int RetryCount = 3;

    /// <summary>Registers <see cref="AppDbContext"/> and <see cref="ArchiveDbContext"/>.</summary>
    /// <param name="services">The service collection.</param>
    /// <param name="config">Application configuration.</param>
    /// <returns>The same collection, for chaining.</returns>
    /// <exception cref="UnknownProviderException">The named provider is not one this build has migrations for.</exception>
    public static IServiceCollection AddDatabases(this IServiceCollection services, IConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(config);

        var provider = ResolveProvider(config);
        var migrationsAssembly = DatabaseProviders.MigrationsAssembly(provider);

        var connectionString = Required(config, "ConnectionStrings:Default");
        var archiveConnectionString = Required(config, "ConnectionStrings:Archive");

        services.Configure<DatabaseOptions>(config.GetSection(DatabaseOptions.SectionName));

        services.AddDbContext<AppDbContext>(opt =>
            Configure(opt, provider, connectionString, migrationsAssembly));

        // The archive has no migrations of its own history to name - its schema is created by the
        // same assembly - but it needs the same provider and the same retry policy.
        services.AddDbContext<ArchiveDbContext>(opt =>
            Configure(opt, provider, archiveConnectionString, migrationsAssembly));

        return services;
    }

    /// <summary>Reads and validates the provider name, in canonical form.</summary>
    /// <param name="config">Application configuration.</param>
    /// <returns>The canonical provider key.</returns>
    /// <exception cref="UnknownProviderException">The name is not one this build knows.</exception>
    public static string ResolveProvider(IConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var configured = config.GetSection(DatabaseOptions.SectionName).Get<DatabaseOptions>() ?? new DatabaseOptions();
        var provider = ProviderKey.Normalize(configured.Provider);

        if (!DatabaseProviders.All.Contains(provider, StringComparer.Ordinal))
            throw new UnknownProviderException("database", configured.Provider, DatabaseProviders.All);

        return provider;
    }

    private static void Configure(
        DbContextOptionsBuilder opt, string provider, string connectionString, string migrationsAssembly)
    {
        // EnableRetryOnFailure on both: transient faults (failover, deadlock victim, pool timeout)
        // are routine for a database in a container, and without an execution strategy each one
        // surfaces as a consumer exception and burns the message's retry budget.
        switch (provider)
        {
            case DatabaseProviders.SqlServer:
                opt.UseSqlServer(connectionString, sql =>
                {
                    sql.MigrationsAssembly(migrationsAssembly);
                    sql.EnableRetryOnFailure(RetryCount, RetryDelay, null);
                });
                break;

            case DatabaseProviders.PostgreSql:
                opt.UseNpgsql(connectionString, npgsql =>
                {
                    npgsql.MigrationsAssembly(migrationsAssembly);
                    npgsql.EnableRetryOnFailure(RetryCount, RetryDelay, null);
                });
                break;

            default:
                throw new UnknownProviderException("database", provider, DatabaseProviders.All);
        }
    }

    /// <summary>Names the setting and its environment form, so the fix is obvious from the log line.</summary>
    private static string Required(IConfiguration config, string key) =>
        config[key] is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException(
                $"{key} is not configured. Set {key.Replace(':', '_').Replace("_", "__")} in the environment.");
}
