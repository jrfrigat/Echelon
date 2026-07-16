using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using ReleaseOrchestrator.Infrastructure.Persistence;

namespace ReleaseOrchestrator.Migrations.MsSql;

public class AppDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<AppDbContext>();
        optionsBuilder.UseSqlServer(
            "Server=localhost;Database=ReleaseOrchestrator;User Id=sa;Password=Dev_Password1;TrustServerCertificate=True",
            sql => sql.MigrationsAssembly(typeof(AppDbContextFactory).Assembly.FullName));

        return new AppDbContext(optionsBuilder.Options);
    }
}
