using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Echelon.Infrastructure.Archive;
using Echelon.Infrastructure.Persistence;

namespace Echelon.Migrations.MsSql;

/// <summary>
/// Design-time connection for `dotnet ef`, taken from the environment. This used to be a
/// hard-coded localhost string carrying a shared dev password, so the tooling could not be
/// pointed at CI or staging without editing source - and the password shipped in git.
///
/// `migrations add` never opens a connection, so the LocalDB fallback suffices for authoring.
/// `database update` does connect: set the variable then.
/// </summary>
internal static class DesignTimeConnection
{
    public static string Resolve(string variableName) =>
        Environment.GetEnvironmentVariable(variableName)
        ?? "Server=(localdb)\\MSSQLLocalDB;Database=Echelon;Trusted_Connection=True;TrustServerCertificate=True";
}

public class AppDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlServer(
                DesignTimeConnection.Resolve("ConnectionStrings__Default"),
                sql => sql.MigrationsAssembly(typeof(AppDbContextFactory).Assembly.GetName().Name))
            .Options;

        return new AppDbContext(options);
    }
}

/// <summary>
/// The archive database gets real migrations too. It was created with EnsureCreated, which
/// bypasses the migrations history entirely and leaves the schema with no way to evolve -
/// unworkable for a store the README expects to hold two years of history.
/// </summary>
public class ArchiveDbContextFactory : IDesignTimeDbContextFactory<ArchiveDbContext>
{
    public ArchiveDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<ArchiveDbContext>()
            .UseSqlServer(
                DesignTimeConnection.Resolve("ConnectionStrings__Archive"),
                sql => sql.MigrationsAssembly(typeof(ArchiveDbContextFactory).Assembly.GetName().Name))
            .Options;

        return new ArchiveDbContext(options);
    }
}
