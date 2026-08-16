using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using ReleaseOrchestrator.Infrastructure.Archive;
using ReleaseOrchestrator.Infrastructure.Persistence;

namespace ReleaseOrchestrator.Migrations.Postgres;

/// <summary>
/// Design-time connection for `dotnet ef`, taken from the environment - the same rule as the SQL
/// Server side, for the same reason: the tooling has to be pointable at CI or staging without
/// editing source, and no password belongs in git.
/// </summary>
/// <remarks>
/// `migrations add` never opens a connection, so the fallback is only there to let authoring work
/// on a clean clone. `database update` does connect: set the variable then.
/// </remarks>
internal static class DesignTimeConnection
{
    public static string Resolve(string variableName) =>
        Environment.GetEnvironmentVariable(variableName)
        ?? "Host=localhost;Database=releaseorchestrator;Username=postgres;Password=postgres";
}

/// <summary>Design-time <see cref="AppDbContext"/> for the PostgreSQL migrations.</summary>
public class AppDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    /// <inheritdoc/>
    public AppDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(
                DesignTimeConnection.Resolve("ConnectionStrings__Default"),
                npgsql => npgsql.MigrationsAssembly(typeof(AppDbContextFactory).Assembly.GetName().Name))
            .Options;

        return new AppDbContext(options);
    }
}

/// <summary>Design-time <see cref="ArchiveDbContext"/> for the PostgreSQL migrations.</summary>
public class ArchiveDbContextFactory : IDesignTimeDbContextFactory<ArchiveDbContext>
{
    /// <inheritdoc/>
    public ArchiveDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<ArchiveDbContext>()
            .UseNpgsql(
                DesignTimeConnection.Resolve("ConnectionStrings__Archive"),
                npgsql => npgsql.MigrationsAssembly(typeof(ArchiveDbContextFactory).Assembly.GetName().Name))
            .Options;

        return new ArchiveDbContext(options);
    }
}
