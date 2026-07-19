using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using ReleaseOrchestrator.Infrastructure.Persistence.Models;
using ReleaseOrchestrator.Infrastructure.Persistence;
using ReleaseOrchestrator.Infrastructure.Tracker;
using Xunit;

namespace ReleaseOrchestrator.UnitTests.Tracker;

/// <summary>
/// TrackerService produces every TaskDependency row, and therefore every task edge in the release
/// plan. Nothing called it for the whole life of the project, so the table stayed empty and plans
/// were ordered by stack links alone — the algorithm was correct and the data never arrived.
/// These are the tests that would have caught that.
/// </summary>
public sealed class TrackerServiceTests : IAsyncLifetime
{
    private static readonly DateTime Now = new(2026, 7, 17, 12, 0, 0, DateTimeKind.Utc);
    private static CancellationToken Ct => CancellationToken.None;

    private SqliteConnection _connection = null!;
    private AppDbContext _db = null!;
    private TrackerConnection _tracker = null!;

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        await _connection.OpenAsync(Ct);

        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);
        await _db.Database.EnsureCreatedAsync(Ct);

        _tracker = new TrackerConnection
        {
            Id = Guid.NewGuid(),
            Name = "tracker",
            ProviderType = "fake",
            ApiUrl = "https://tracker.example.com"
        };
        _db.TrackerConnections.Add(_tracker);
        await _db.SaveChangesAsync(Ct);
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        await _connection.DisposeAsync();
    }

    private TrackerService Service(FakeTrackerProvider provider) =>
        new(_db, new FakeTrackerProviderFactory(provider), new FakeTimeProvider(Now), NullLogger<TrackerService>.Instance);

    private Task<TaskItem?> FindTaskAsync(string key) =>
        _db.Tasks.FirstOrDefaultAsync(t => t.ExternalId == key, Ct);

    [Fact]
    public async Task ImportsTheTaskAndItsDependencyEdge()
    {
        var provider = new FakeTrackerProvider()
            .WithIssue("TASK-1")
            .WithIssue("TASK-2")
            .WithDependencies("TASK-2", "TASK-1");

        var changed = await Service(provider).SyncTaskAsync(_tracker.Id, "TASK-2", Ct);

        Assert.True(changed);

        var dependent = await FindTaskAsync("TASK-2");
        var prerequisite = await FindTaskAsync("TASK-1");
        Assert.NotNull(dependent);
        Assert.NotNull(prerequisite);

        // The row reads "TASK-2 depends on TASK-1". Getting this backwards is what collapsed every
        // edge into a self-loop and inverted the deploy order.
        var edge = Assert.Single(await _db.TaskDependencies.ToListAsync(Ct));
        Assert.Equal(dependent!.Id, edge.DependentTaskId);
        Assert.Equal(prerequisite!.Id, edge.DependsOnTaskId);
    }

    /// <summary>
    /// Sync order is not something we control, so a task referencing one we have not imported is
    /// ordinary. Skipping it — as the original `if (depTask is null) continue` did — loses the edge
    /// for good, because nothing revisits the dependent once the prerequisite appears.
    /// </summary>
    [Fact]
    public async Task FetchesAPrerequisiteThatIsNotStoredYet()
    {
        var provider = new FakeTrackerProvider()
            .WithIssue("TASK-1")
            .WithIssue("TASK-2")
            .WithDependencies("TASK-2", "TASK-1");

        await Service(provider).SyncTaskAsync(_tracker.Id, "TASK-2", Ct);

        Assert.NotNull(await FindTaskAsync("TASK-1"));
        Assert.Single(await _db.TaskDependencies.ToListAsync(Ct));
    }

    /// <summary>
    /// The prerequisite is fetched shallowly: its own links are its own sync's job. Walking them
    /// here would turn one sync into a crawl of the whole graph.
    /// </summary>
    [Fact]
    public async Task DoesNotWalkThePrerequisitesOwnDependencies()
    {
        var provider = new FakeTrackerProvider()
            .WithIssue("TASK-1").WithIssue("TASK-2").WithIssue("TASK-3")
            .WithDependencies("TASK-3", "TASK-2")
            .WithDependencies("TASK-2", "TASK-1");

        await Service(provider).SyncTaskAsync(_tracker.Id, "TASK-3", Ct);

        // TASK-1 is two hops away and must not be imported by TASK-3's sync.
        Assert.Null(await FindTaskAsync("TASK-1"));
        Assert.Single(await _db.TaskDependencies.ToListAsync(Ct));
    }

    [Fact]
    public async Task ADependencyOnAnIssueTheTrackerDoesNotHaveIsSkipped()
    {
        var provider = new FakeTrackerProvider()
            .WithIssue("TASK-2")
            .WithDependencies("TASK-2", "GHOST-9");

        var changed = await Service(provider).SyncTaskAsync(_tracker.Id, "TASK-2", Ct);

        Assert.False(changed);
        Assert.Empty(await _db.TaskDependencies.ToListAsync(Ct));
        Assert.NotNull(await FindTaskAsync("TASK-2"));
    }

    [Fact]
    public async Task RemovedEdgesGoAndSurvivingOnesStay()
    {
        var provider = new FakeTrackerProvider()
            .WithIssue("TASK-1").WithIssue("TASK-2").WithIssue("TASK-3")
            .WithDependencies("TASK-3", "TASK-1", "TASK-2");

        var service = Service(provider);
        await service.SyncTaskAsync(_tracker.Id, "TASK-3", Ct);
        Assert.Equal(2, await _db.TaskDependencies.CountAsync(Ct));

        provider.WithDependencies("TASK-3", "TASK-1");
        var changed = await service.SyncTaskAsync(_tracker.Id, "TASK-3", Ct);

        Assert.True(changed);
        var edge = Assert.Single(await _db.TaskDependencies.ToListAsync(Ct));
        Assert.Equal((await FindTaskAsync("TASK-1"))!.Id, edge.DependsOnTaskId);
    }

    /// <summary>
    /// Only a real change replans. Reporting "changed" on every sync would make the periodic
    /// reconciliation rebuild the plan every half hour for nothing.
    /// </summary>
    [Fact]
    public async Task ResyncingWithTheSameEdgesReportsNoChange()
    {
        var provider = new FakeTrackerProvider()
            .WithIssue("TASK-1").WithIssue("TASK-2")
            .WithDependencies("TASK-2", "TASK-1");

        var service = Service(provider);
        Assert.True(await service.SyncTaskAsync(_tracker.Id, "TASK-2", Ct));
        Assert.False(await service.SyncTaskAsync(_tracker.Id, "TASK-2", Ct));
    }

    /// <summary>
    /// ClosedAt is derived from the status, not taken from ResolvedAt: a tracker can report a
    /// resolution time for a status we do not treat as closed, and archiving keys off this column.
    /// </summary>
    [Fact]
    public async Task ClosedStatusSetsClosedAt()
    {
        var provider = new FakeTrackerProvider().WithIssue("TASK-1", status: "resolved", resolvedAt: Now.AddDays(-2));

        await Service(provider).SyncTaskAsync(_tracker.Id, "TASK-1", Ct);

        var task = await FindTaskAsync("TASK-1");
        Assert.Equal(Now.AddDays(-2), task!.ClosedAt);
    }

    [Fact]
    public async Task ReopeningClearsClosedAt()
    {
        var provider = new FakeTrackerProvider().WithIssue("TASK-1", status: "closed", resolvedAt: Now.AddDays(-2));
        var service = Service(provider);
        await service.SyncTaskAsync(_tracker.Id, "TASK-1", Ct);
        Assert.NotNull((await FindTaskAsync("TASK-1"))!.ClosedAt);

        provider.WithIssue("TASK-1", status: "open");
        await service.SyncTaskAsync(_tracker.Id, "TASK-1", Ct);

        // A reopened task that keeps ClosedAt is archived out from under a live release plan.
        Assert.Null((await FindTaskAsync("TASK-1"))!.ClosedAt);
    }

    /// <summary>
    /// A closed status with no resolution time still has to get one, or the task is closed and
    /// never archivable — which is exactly what happened to "resolved" tasks.
    /// </summary>
    [Fact]
    public async Task ClosedStatusWithoutAResolutionTimeFallsBackToNow()
    {
        var provider = new FakeTrackerProvider().WithIssue("TASK-1", status: "closed", resolvedAt: null);

        await Service(provider).SyncTaskAsync(_tracker.Id, "TASK-1", Ct);

        Assert.Equal(Now, (await FindTaskAsync("TASK-1"))!.ClosedAt);
    }

    [Fact]
    public async Task AnIssueTheTrackerDoesNotHaveImportsNothing()
    {
        var changed = await Service(new FakeTrackerProvider()).SyncTaskAsync(_tracker.Id, "GHOST-1", Ct);

        Assert.False(changed);
        Assert.Empty(await _db.Tasks.ToListAsync(Ct));
    }

    // ---- hierarchy ------------------------------------------------------------

    [Fact]
    public async Task ImportsTheParentLink()
    {
        var provider = new FakeTrackerProvider()
            .WithIssue("EPIC-1")
            .WithIssue("TASK-2", parentKey: "EPIC-1");

        var changed = await Service(provider).SyncTaskAsync(_tracker.Id, "TASK-2", Ct);

        Assert.True(changed);

        var child = await FindTaskAsync("TASK-2");
        var parent = await FindTaskAsync("EPIC-1");
        Assert.NotNull(parent);
        Assert.Equal(parent!.Id, child!.ParentTaskId);
    }

    /// <summary>
    /// Same reasoning as a prerequisite: sync order is not ours to control, and dropping the link
    /// because the parent has not been imported yet loses it for good.
    /// </summary>
    [Fact]
    public async Task FetchesAParentThatIsNotStoredYet()
    {
        var provider = new FakeTrackerProvider()
            .WithIssue("EPIC-1")
            .WithIssue("TASK-2", parentKey: "EPIC-1");

        await Service(provider).SyncTaskAsync(_tracker.Id, "TASK-2", Ct);

        Assert.NotNull(await FindTaskAsync("EPIC-1"));
    }

    /// <summary>
    /// The parent is fetched shallowly. Resolving its parent too would walk the hierarchy to its root
    /// on every sync — and never terminate at all if a tracker reported a cycle.
    /// </summary>
    [Fact]
    public async Task DoesNotWalkTheParentsOwnParent()
    {
        var provider = new FakeTrackerProvider()
            .WithIssue("EPIC-1")
            .WithIssue("EPIC-2", parentKey: "EPIC-1")
            .WithIssue("TASK-3", parentKey: "EPIC-2");

        await Service(provider).SyncTaskAsync(_tracker.Id, "TASK-3", Ct);

        // EPIC-1 is two hops up; TASK-3's sync must not import it.
        Assert.Null(await FindTaskAsync("EPIC-1"));
        Assert.Null((await FindTaskAsync("EPIC-2"))!.ParentTaskId);
    }

    [Fact]
    public async Task AParentTheTrackerDoesNotHaveIsSkipped()
    {
        var provider = new FakeTrackerProvider().WithIssue("TASK-2", parentKey: "GHOST-9");

        await Service(provider).SyncTaskAsync(_tracker.Id, "TASK-2", Ct);

        Assert.Null((await FindTaskAsync("TASK-2"))!.ParentTaskId);
    }

    /// <summary>A task moved out from under its epic loses the link, or it deploys against an order nobody states.</summary>
    [Fact]
    public async Task ClearingTheParentInTheTrackerClearsItHere()
    {
        var provider = new FakeTrackerProvider()
            .WithIssue("EPIC-1")
            .WithIssue("TASK-2", parentKey: "EPIC-1");

        var service = Service(provider);
        await service.SyncTaskAsync(_tracker.Id, "TASK-2", Ct);
        Assert.NotNull((await FindTaskAsync("TASK-2"))!.ParentTaskId);

        provider.WithIssue("TASK-2", parentKey: null);
        var changed = await service.SyncTaskAsync(_tracker.Id, "TASK-2", Ct);

        Assert.True(changed);
        Assert.Null((await FindTaskAsync("TASK-2"))!.ParentTaskId);
    }

    [Fact]
    public async Task ResyncingWithTheSameParentReportsNoChange()
    {
        var provider = new FakeTrackerProvider()
            .WithIssue("EPIC-1")
            .WithIssue("TASK-2", parentKey: "EPIC-1");

        var service = Service(provider);
        Assert.True(await service.SyncTaskAsync(_tracker.Id, "TASK-2", Ct));
        Assert.False(await service.SyncTaskAsync(_tracker.Id, "TASK-2", Ct));
    }

    /// <summary>A task that is its own parent would be a self-edge in the plan, exactly as a self-dependency is.</summary>
    [Fact]
    public async Task SelfParentIsIgnored()
    {
        var provider = new FakeTrackerProvider().WithIssue("TASK-1", parentKey: "TASK-1");

        await Service(provider).SyncTaskAsync(_tracker.Id, "TASK-1", Ct);

        Assert.Null((await FindTaskAsync("TASK-1"))!.ParentTaskId);
    }

    [Fact]
    public async Task SelfDependencyIsIgnored()
    {
        var provider = new FakeTrackerProvider()
            .WithIssue("TASK-1")
            .WithDependencies("TASK-1", "TASK-1");

        await Service(provider).SyncTaskAsync(_tracker.Id, "TASK-1", Ct);

        // A self-edge carries no ordering and would strand the task in Kahn's algorithm.
        Assert.Empty(await _db.TaskDependencies.ToListAsync(Ct));
    }
}
