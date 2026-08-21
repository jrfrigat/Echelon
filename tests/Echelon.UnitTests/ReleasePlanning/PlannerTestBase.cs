using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Echelon.Infrastructure.Persistence.Models;
using Echelon.Core.Enums;
using Echelon.Infrastructure.Persistence;
using Echelon.Infrastructure.ReleasePlanning;
using Xunit;

namespace Echelon.UnitTests.ReleasePlanning;

/// <summary>
/// A planner over a real (if in-memory) database, with builders for the graph it reads.
/// </summary>
/// <remarks>
/// The planner is worth testing against a database rather than a hand-built list precisely because
/// its Include chain is what fails: drop a ThenInclude and the navigation is empty, not an error.
/// A test that constructs the object graph in memory asserts the algorithm and nothing else.
/// </remarks>
public abstract class PlannerTestBase : IAsyncLifetime
{
    protected static readonly DateTime Now = new(2026, 7, 17, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>xunit v2 has no ambient test cancellation; these tests are in-memory and fast.</summary>
    protected static CancellationToken Ct => CancellationToken.None;

    private SqliteConnection _connection = null!;

    protected AppDbContext Db { get; private set; } = null!;
    protected TrackerConnection Tracker { get; private set; } = null!;
    protected VcsConnection Vcs { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        // In-memory, but a real connection: an in-memory SQLite database lives exactly as long as
        // its last connection, and EF opens and closes as it pleases. Holding one keeps the schema.
        _connection = new SqliteConnection("DataSource=:memory:");
        await _connection.OpenAsync(Ct);

        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection, ConfigureSqlite);
        Db = new AppDbContext(options.Options);
        await Db.Database.EnsureCreatedAsync(Ct);

        Tracker = new TrackerConnection
        {
            Id = Guid.NewGuid(),
            Name = "tracker",
            ProviderType = "fake",
            ApiUrl = "https://tracker.example.com"
        };
        Vcs = new VcsConnection
        {
            Id = Guid.NewGuid(),
            Name = "vcs",
            ProviderType = "gitlab",
            ApiUrl = "https://gitlab.example.com"
        };
        Db.TrackerConnections.Add(Tracker);
        Db.VcsConnections.Add(Vcs);
        await Db.SaveChangesAsync(Ct);
    }

    /// <summary>Hook for a test that needs the provider configured differently.</summary>
    /// <param name="sqlite">The SQLite options being built.</param>
    /// <remarks>
    /// Exists for <c>PlanWriteRetryTests</c>, which needs a retrying execution strategy - the thing
    /// SQLite does not have and SQL Server does, and therefore the one difference that let a broken
    /// transaction pass every test here and fail on a real deployment.
    /// </remarks>
    protected virtual void ConfigureSqlite(SqliteDbContextOptionsBuilder sqlite)
    {
    }

    public async Task DisposeAsync()
    {
        await Db.DisposeAsync();
        await _connection.DisposeAsync();
    }

    protected Repository AddRepository(string name)
    {
        var repo = new Repository
        {
            Id = Guid.NewGuid(),
            Name = name,
            ExternalId = $"group/{name}",
            ConnectionId = Vcs.Id
        };
        Db.Repositories.Add(repo);
        return repo;
    }

    protected TaskItem AddTask(string externalId)
    {
        var task = new TaskItem
        {
            Id = Guid.NewGuid(),
            ExternalId = externalId,
            Title = externalId,
            Status = "open",
            TrackerConnectionId = Tracker.Id
        };
        Db.Tasks.Add(task);
        return task;
    }

    /// <summary>Records that <paramref name="dependent"/> waits on <paramref name="dependsOn"/>.</summary>
    protected void AddTaskDependency(TaskItem dependent, TaskItem dependsOn) =>
        Db.TaskDependencies.Add(new TaskDependency
        {
            Id = Guid.NewGuid(),
            DependentTaskId = dependent.Id,
            DependsOnTaskId = dependsOn.Id
        });

    /// <summary>
    /// Makes <paramref name="child"/> a subtask of <paramref name="parent"/>, so the child deploys
    /// first and the parent last.
    /// </summary>
    protected static void AddChild(TaskItem parent, TaskItem child) =>
        child.ParentTaskId = parent.Id;

    /// <summary>Records that <paramref name="from"/>'s repository deploys after <paramref name="to"/>'s.</summary>
    protected void AddRepositoryDependency(Repository from, Repository to, StackDependencyType type) =>
        Db.RepositoryDependencies.Add(new RepositoryDependency
        {
            Id = Guid.NewGuid(),
            FromRepositoryId = from.Id,
            ToRepositoryId = to.Id,
            Type = type
        });

    protected MergeRequest AddMergeRequest(
        Repository repository,
        TaskItem? task = null,
        MergeRequestStatus status = MergeRequestStatus.ReadyForDeploy,
        DateTime? createdAt = null,
        string? externalId = null)
    {
        var mr = new MergeRequest
        {
            Id = Guid.NewGuid(),
            ExternalId = externalId ?? $"{Db.MergeRequests.Local.Count + 1}",
            SourceBranch = task is null ? "feature/x" : $"feature/{task.ExternalId}",
            TargetBranch = "main",
            RepositoryId = repository.Id,
            TaskId = task?.Id,
            Status = status,
            CreatedAt = createdAt ?? Now.AddDays(-1)
        };
        Db.MergeRequests.Add(mr);
        return mr;
    }
}
