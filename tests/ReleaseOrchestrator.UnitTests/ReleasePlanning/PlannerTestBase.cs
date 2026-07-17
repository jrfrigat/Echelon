using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using ReleaseOrchestrator.Core.Entities;
using ReleaseOrchestrator.Core.Enums;
using ReleaseOrchestrator.Infrastructure.Persistence;
using ReleaseOrchestrator.Infrastructure.ReleasePlanning;
using Xunit;

namespace ReleaseOrchestrator.UnitTests.ReleasePlanning;

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

        Db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);
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

    public async Task DisposeAsync()
    {
        await Db.DisposeAsync();
        await _connection.DisposeAsync();
    }

    protected ReleasePlanner Planner() =>
        new(Db, new FakeTimeProvider(Now), NullLogger<ReleasePlanner>.Instance);

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

    protected Stack AddStack(string name, params Repository[] repositories)
    {
        var stack = new Stack { Id = Guid.NewGuid(), Name = name };
        Db.Stacks.Add(stack);
        foreach (var repo in repositories)
            Db.Set<RepositoryStack>().Add(new RepositoryStack { RepositoryId = repo.Id, StackId = stack.Id });
        return stack;
    }

    /// <summary>Records that <paramref name="from"/> deploys after <paramref name="to"/>.</summary>
    protected void AddStackDependency(Stack from, Stack to, StackDependencyType type) =>
        Db.Set<StackDependency>().Add(new StackDependency
        {
            Id = Guid.NewGuid(),
            FromStackId = from.Id,
            ToStackId = to.Id,
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

    protected static List<Guid> StageItems(Application.DTOs.ReleasePlanDto plan, int sequence) =>
        plan.Stages.Single(s => s.Sequence == sequence).Items.Select(i => i.MergeRequestId).ToList();
}
