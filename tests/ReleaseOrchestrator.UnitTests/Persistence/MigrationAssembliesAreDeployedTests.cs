using System.Reflection;
using Microsoft.EntityFrameworkCore.Migrations;
using ReleaseOrchestrator.Infrastructure.Persistence;
using Xunit;

namespace ReleaseOrchestrator.UnitTests.Persistence;

/// <summary>
/// Every provider's migrations assembly must actually be deployed with the host.
/// </summary>
/// <remarks>
/// <para>
/// The database provider is chosen at runtime from configuration, and the migrations assembly is
/// named as a STRING (<see cref="DatabaseProviders.MigrationsAssembly"/>) that EF loads by name. So a
/// host that forgets to reference one compiles, starts, serves - and then dies the moment it is
/// pointed at that provider with startup migration on.
/// </para>
/// <para>
/// Which is what happened: only the SQL Server assembly was referenced, so a PostgreSQL deployment
/// (docker-compose sets <c>Database__MigrateOnStartup=true</c>) failed at startup with
/// <c>FileNotFoundException: ReleaseOrchestrator.Migrations.Postgres</c>. Found by running the
/// application, because nothing else could see it - not the compiler, not the model tests, not a
/// migration applied through the CLI, which loads the assembly from its own project.
/// </para>
/// <para>
/// The test project references the web host, so its output folder holds what the host's does.
/// </para>
/// </remarks>
public class MigrationAssembliesAreDeployedTests
{
    [Theory]
    [InlineData(DatabaseProviders.SqlServer)]
    [InlineData(DatabaseProviders.PostgreSql)]
    public void EveryProvidersMigrationsAssemblyLoads(string provider)
    {
        var name = DatabaseProviders.MigrationsAssembly(provider);

        var exception = Record.Exception(() => Assembly.Load(new AssemblyName(name)));

        Assert.True(exception is null,
            $"'{name}' is not deployed, so '{provider}' cannot migrate at startup. "
            + $"Add a ProjectReference to it from every host. ({exception?.Message})");
    }

    /// <summary>
    /// Loading is not enough: the assembly must actually contain migrations.
    /// </summary>
    /// <remarks>
    /// An empty or wrongly-named assembly loads fine and then reports "no migrations to apply",
    /// which looks like success on a database that has no schema at all.
    /// </remarks>
    [Theory]
    [InlineData(DatabaseProviders.SqlServer)]
    [InlineData(DatabaseProviders.PostgreSql)]
    public void EveryProvidersMigrationsAssemblyHasMigrations(string provider)
    {
        var assembly = Assembly.Load(new AssemblyName(DatabaseProviders.MigrationsAssembly(provider)));

        var migrations = assembly.GetTypes()
            .Where(t => typeof(Migration).IsAssignableFrom(t) && !t.IsAbstract)
            .ToList();

        Assert.True(migrations.Count > 0, $"'{assembly.GetName().Name}' contains no migrations.");
    }
}
