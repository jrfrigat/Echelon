using Echelon.Core.Enums;
using Echelon.Infrastructure.Persistence;
using Echelon.Infrastructure.Persistence.Models;
using Echelon.Infrastructure.ReleasePlanning;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Echelon.UnitTests.ReleasePlanning;

/// <summary>
/// The planner against a real SQL Server, registered exactly as the host registers it.
/// </summary>
/// <remarks>
/// <para>
/// Opt-in, because CI has no SQL Server: set <c>ECHELON_SQLSERVER_TESTS=1</c> and a local instance is
/// used (LocalDB by default, or <c>ECHELON_SQLSERVER</c> for another). Everything else in this suite
/// runs on SQLite, which is the right default - and also why this file exists.
/// </para>
/// <para>
/// Two defects reached a running deployment because SQLite cannot express them, and both are pinned
/// here. The retrying execution strategy refuses a transaction it did not open, so recalculation
/// failed with a 500 on every real database; and <c>Repository.ExternalId</c> carries a
/// case-sensitive collation while the connection name inherits the server's, so building a merge
/// request key by concatenating them in SQL is a collation conflict - which broke export, validate
/// and import together. SQLite has neither a retrying strategy nor more than one collation, so both
/// passed every test that existed.
/// </para>
/// </remarks>
public class SqlServerPlanWriteTests
{
    private const string DatabaseName = "EchelonPlanWriteTests";

    private static string Server =>
        Environment.GetEnvironmentVariable("ECHELON_SQLSERVER")
        ?? @"Server=(localdb)\MSSQLLocalDB;Integrated Security=true;TrustServerCertificate=True";

    [RequiresSqlServer]
    public async Task RecalculatesTwiceAndImportsBack()
    {
        var ct = CancellationToken.None;
        await RecreateDatabaseAsync(ct);

        var connectionString = $"{Server};Database={DatabaseName}";
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Database:Provider"] = DatabaseProviders.SqlServer,
            ["ConnectionStrings:Default"] = connectionString,
            ["ConnectionStrings:Archive"] = connectionString
        }).Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDatabases(config);

        await using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        // Registered by the host's own setup, so this is the retry policy production runs with - the
        // condition under which a self-opened transaction is refused.
        Assert.True(db.Database.CreateExecutionStrategy().RetriesOnFailure);

        await db.Database.EnsureCreatedAsync(ct);
        var parent = await SeedAsync(db, ct);

        var planner = new RolloutPlanner(db, TimeProvider.System, NullLogger<RolloutPlanner>.Instance);

        var first = await planner.RecalculateAsync(parent, actor: null, ct);
        var second = await planner.RecalculateAsync(parent, actor: null, ct);

        // Export and import go through the key resolution that mixed two collations in one SQL
        // expression; on this provider that was an error, not a slow query.
        var document = await planner.ExportPlanYamlAsync(parent, ct);
        var imported = await planner.ImportPlanAsync(parent, document!, force: false, actor: null, ct);

        Assert.Equal(1, first.Version);
        Assert.Equal(2, second.Version);
        Assert.True(imported.Accepted);

        // Three versions written, exactly one of them active: the deactivate-and-insert pair still
        // commits as a unit now that the transaction lives inside the execution strategy.
        Assert.Equal(3, await db.RolloutPlans.CountAsync(p => p.TargetTaskId == parent, ct));
        Assert.Single(await db.RolloutPlans.Where(p => p.TargetTaskId == parent && p.IsActive).ToListAsync(ct));
    }

    /// <summary>A parent task and its subtask, each with a merge request in its own repository.</summary>
    /// <returns>The parent task's id - the plan target.</returns>
    private static async Task<Guid> SeedAsync(AppDbContext db, CancellationToken ct)
    {
        var tracker = new TrackerConnection
        {
            Id = Guid.NewGuid(), Name = "tracker", ProviderType = "fake", ApiUrl = "https://tracker.example.com"
        };
        var vcs = new VcsConnection
        {
            Id = Guid.NewGuid(), Name = "vcs", ProviderType = "gitlab", ApiUrl = "https://gitlab.example.com"
        };
        db.TrackerConnections.Add(tracker);
        db.VcsConnections.Add(vcs);

        var parent = new TaskItem
        {
            Id = Guid.NewGuid(), ExternalId = "ECH-1", Title = "parent", Status = "open", TrackerConnectionId = tracker.Id
        };
        var child = new TaskItem
        {
            Id = Guid.NewGuid(), ExternalId = "ECH-2", Title = "child", Status = "open",
            TrackerConnectionId = tracker.Id, ParentTaskId = parent.Id
        };
        db.Tasks.AddRange(parent, child);

        var api = new Repository { Id = Guid.NewGuid(), Name = "api", ExternalId = "group/api", ConnectionId = vcs.Id };
        var web = new Repository { Id = Guid.NewGuid(), Name = "web", ExternalId = "group/web", ConnectionId = vcs.Id };
        db.Repositories.AddRange(api, web);

        db.MergeRequests.AddRange(
            NewMergeRequest("1", api.Id, parent.Id),
            NewMergeRequest("2", web.Id, child.Id));

        await db.SaveChangesAsync(ct);
        return parent.Id;
    }

    private static MergeRequest NewMergeRequest(string externalId, Guid repositoryId, Guid taskId) =>
        new()
        {
            Id = Guid.NewGuid(),
            ExternalId = externalId,
            SourceBranch = $"feature/{externalId}",
            TargetBranch = "main",
            RepositoryId = repositoryId,
            TaskId = taskId,
            Status = MergeRequestStatus.ReadyForDeploy,
            CreatedAt = DateTime.UtcNow.AddDays(-1)
        };

    /// <summary>Drops and recreates the scratch database, so a run never inherits the last one's rows.</summary>
    private static async Task RecreateDatabaseAsync(CancellationToken ct)
    {
        await using var admin = new SqlConnection($"{Server};Database=master");
        await admin.OpenAsync(ct);

        await using var command = admin.CreateCommand();
        command.CommandText =
            $"IF DB_ID('{DatabaseName}') IS NOT NULL BEGIN "
            + $"ALTER DATABASE [{DatabaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; "
            + $"DROP DATABASE [{DatabaseName}]; END; "
            + $"CREATE DATABASE [{DatabaseName}];";
        await command.ExecuteNonQueryAsync(ct);
    }
}

/// <summary>A fact that runs only where a SQL Server has been offered.</summary>
/// <remarks>
/// Skipped rather than absent, so the reason is visible in the run: a test that silently does not
/// exist is one nobody remembers to run. Weir gates its container tests the same way.
/// </remarks>
[AttributeUsage(AttributeTargets.Method)]
public sealed class RequiresSqlServerAttribute : FactAttribute
{
    /// <summary>The variable that opts a machine in.</summary>
    public const string EnvironmentVariable = "ECHELON_SQLSERVER_TESTS";

    /// <summary>Marks the test skipped unless <see cref="EnvironmentVariable"/> is set to 1.</summary>
    public RequiresSqlServerAttribute()
    {
        if (Environment.GetEnvironmentVariable(EnvironmentVariable) != "1")
        {
            Skip = $"Set {EnvironmentVariable}=1 to run against a local SQL Server.";
        }
    }
}
