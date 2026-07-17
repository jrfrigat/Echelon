using MassTransit;
using MassTransit.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using ReleaseOrchestrator.Application.Contracts.Messages;
using ReleaseOrchestrator.Infrastructure.Persistence;
using ReleaseOrchestrator.Infrastructure.Persistence.Models;
using ReleaseOrchestrator.Infrastructure.Queue.Consumers;
using ReleaseOrchestrator.Providers.Abstractions.Tracker;
using ReleaseOrchestrator.UnitTests.Tracker;
using Xunit;

namespace ReleaseOrchestrator.UnitTests.Queue;

/// <summary>
/// How a task enters the system and how its status is applied.
/// </summary>
/// <remarks>
/// Both consumers publish TaskSyncRequested unconditionally, and that is the point rather than
/// noise: a tracker webhook carries the title and status but never the issue's links, and the
/// links are the only thing task dependencies are made of. Drop the request and the plan is
/// ordered by stack links alone — which is what the service did for its whole life.
/// </remarks>
public sealed class TaskConsumerTests : IAsyncLifetime
{
    private static readonly DateTime Now = new(2026, 7, 17, 12, 0, 0, DateTimeKind.Utc);
    private static CancellationToken Ct => CancellationToken.None;

    private SqliteConnection _connection = null!;
    private AppDbContext _db = null!;
    private ServiceProvider _provider = null!;
    private ITestHarness _harness = null!;

    private TrackerConnection _tracker = null!;

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        await _connection.OpenAsync(Ct);
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);
        await _db.Database.EnsureCreatedAsync(Ct);

        _provider = new ServiceCollection()
            .AddSingleton(_db)
            .AddSingleton<TimeProvider>(new FakeTimeProvider(Now))
            .AddSingleton<ITrackerProviderFactory>(new FakeTrackerProviderFactory(new FakeTrackerProvider()))
            .AddLogging()
            .AddMassTransitTestHarness(x =>
            {
                x.AddConsumer<TaskCreatedConsumer>();
                x.AddConsumer<TaskStatusChangedConsumer>();
            })
            .BuildServiceProvider(true);

        _harness = _provider.GetRequiredService<ITestHarness>();
        await _harness.Start();

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
        await _harness.Stop();
        await _provider.DisposeAsync();
        await _db.DisposeAsync();
        await _connection.DisposeAsync();
    }

    private TaskItem AddTask(string externalId, Guid? trackerId = null, string status = "open", DateTime? closedAt = null)
    {
        var task = new TaskItem
        {
            Id = Guid.NewGuid(),
            ExternalId = externalId,
            Title = externalId,
            Status = status,
            ClosedAt = closedAt,
            TrackerConnectionId = trackerId ?? _tracker.Id
        };
        _db.Tasks.Add(task);
        return task;
    }

    private Task<TaskItem?> FindAsync(string externalId, Guid trackerId) =>
        _db.Tasks.AsNoTracking()
            .FirstOrDefaultAsync(t => t.ExternalId == externalId && t.TrackerConnectionId == trackerId, Ct);

    // ---- TaskCreated ----------------------------------------------------------

    [Fact]
    public async Task ACreatedTaskIsStored()
    {
        await _harness.Bus.Publish(new TaskCreated("tracker", "TASK-1", "Do the thing"), Ct);
        Assert.True(await _harness.Consumed.Any<TaskCreated>());

        var task = await FindAsync("TASK-1", _tracker.Id);
        Assert.NotNull(task);
        Assert.Equal("Do the thing", task.Title);
    }

    /// <summary>
    /// At-least-once delivery means the same created event arrives twice. Inserting again violates
    /// the natural key; the consumer upserts, so a redelivery refreshes the title instead.
    /// </summary>
    [Fact]
    public async Task ARedeliveredCreationUpdatesRatherThanDuplicates()
    {
        await _harness.Bus.Publish(new TaskCreated("tracker", "TASK-1", "First title"), Ct);
        Assert.True(await _harness.Consumed.Any<TaskCreated>());

        await _harness.Bus.Publish(new TaskCreated("tracker", "TASK-1", "Renamed"), Ct);
        await _harness.Consumed.SelectAsync<TaskCreated>().Take(2).ToListAsync();

        var task = Assert.Single(await _db.Tasks.ToListAsync(Ct));
        Assert.Equal("Renamed", task.Title);
    }

    [Fact]
    public async Task AnUnknownTrackerConnectionStoresNothing()
    {
        await _harness.Bus.Publish(new TaskCreated("never-configured", "TASK-1", "Do the thing"), Ct);
        Assert.True(await _harness.Consumed.Any<TaskCreated>());

        Assert.False(await _harness.Consumed.Any<TaskCreated>(x => x.Exception is not null));
        Assert.Empty(await _db.Tasks.ToListAsync(Ct));
    }

    /// <summary>
    /// The links are not in the webhook, and they are the whole of a task dependency.
    /// </summary>
    [Fact]
    public async Task ACreatedTaskAsksForItsLinksToBePulled()
    {
        await _harness.Bus.Publish(new TaskCreated("tracker", "TASK-1", "Do the thing"), Ct);
        Assert.True(await _harness.Consumed.Any<TaskCreated>());

        Assert.True(await _harness.Published.Any<TaskSyncRequested>());
    }

    // ---- TaskStatusChanged ---------------------------------------------------

    [Fact]
    public async Task AStatusChangeIsApplied()
    {
        AddTask("TASK-1");
        await _db.SaveChangesAsync(Ct);

        await _harness.Bus.Publish(new TaskStatusChanged("tracker", "TASK-1", "in progress", null), Ct);
        Assert.True(await _harness.Consumed.Any<TaskStatusChanged>());

        var task = await FindAsync("TASK-1", _tracker.Id);
        Assert.Equal("in progress", task!.Status);
        Assert.Null(task.ClosedAt);
    }

    /// <summary>
    /// Which statuses mean "closed" is the adapter's to know, and it is asked rather than told.
    /// The ingress and this consumer used to keep their own lists, which disagreed about
    /// "resolved" — so a resolved task was closed by one path and open to the other, and stayed
    /// closed-but-unarchivable forever.
    /// </summary>
    [Fact]
    public async Task AClosedStatusIsRecognisedByTheProviderAndStamped()
    {
        AddTask("TASK-1");
        await _db.SaveChangesAsync(Ct);

        await _harness.Bus.Publish(
            new TaskStatusChanged("tracker", "TASK-1", "resolved", Now.AddHours(-2)), Ct);
        Assert.True(await _harness.Consumed.Any<TaskStatusChanged>());

        Assert.Equal(Now.AddHours(-2), (await FindAsync("TASK-1", _tracker.Id))!.ClosedAt);
    }

    /// <summary>
    /// A closed status with no time still needs one, or the task is closed and never archivable.
    /// </summary>
    [Fact]
    public async Task AClosedStatusWithoutATimeFallsBackToNow()
    {
        AddTask("TASK-1");
        await _db.SaveChangesAsync(Ct);

        await _harness.Bus.Publish(new TaskStatusChanged("tracker", "TASK-1", "closed", null), Ct);
        Assert.True(await _harness.Consumed.Any<TaskStatusChanged>());

        Assert.Equal(Now, (await FindAsync("TASK-1", _tracker.Id))!.ClosedAt);
    }

    [Fact]
    public async Task ReopeningClearsTheCloseTime()
    {
        AddTask("TASK-1", status: "closed", closedAt: Now.AddDays(-1));
        await _db.SaveChangesAsync(Ct);

        await _harness.Bus.Publish(new TaskStatusChanged("tracker", "TASK-1", "open", null), Ct);
        Assert.True(await _harness.Consumed.Any<TaskStatusChanged>());

        Assert.Null((await FindAsync("TASK-1", _tracker.Id))!.ClosedAt);
    }

    /// <summary>
    /// Issue keys are unique only within a tracker. Matching on the key alone would stamp one
    /// tracker's status onto another tracker's task.
    /// </summary>
    /// <remarks>
    /// A theory over which tracker is targeted, and that is the whole design of it. Asserting a
    /// single direction passes whenever the unscoped query happens to return the right row first,
    /// which is decided by insertion order rather than by the code under test — the first version
    /// of this test asserted exactly that and survived deleting the scope. Run both ways, an
    /// unscoped query returns the same row twice and one of the two directions has to fail.
    /// </remarks>
    [Theory]
    [InlineData("tracker")]
    [InlineData("other-tracker")]
    public async Task AStatusChangeTouchesOnlyItsOwnTrackersTask(string targetTracker)
    {
        var other = new TrackerConnection
        {
            Id = Guid.NewGuid(),
            Name = "other-tracker",
            ProviderType = "fake",
            ApiUrl = "https://other.example.com"
        };
        _db.TrackerConnections.Add(other);
        AddTask("TASK-1");
        AddTask("TASK-1", other.Id);
        await _db.SaveChangesAsync(Ct);

        await _harness.Bus.Publish(new TaskStatusChanged(targetTracker, "TASK-1", "closed", Now), Ct);
        Assert.True(await _harness.Consumed.Any<TaskStatusChanged>());

        var targetId = targetTracker == "tracker" ? _tracker.Id : other.Id;
        var bystanderId = targetTracker == "tracker" ? other.Id : _tracker.Id;

        Assert.NotNull((await FindAsync("TASK-1", targetId))!.ClosedAt);
        Assert.Null((await FindAsync("TASK-1", bystanderId))!.ClosedAt);
    }

    /// <summary>
    /// The created event is likely still in flight; retry is what resolves the ordering gap.
    /// </summary>
    [Fact]
    public async Task AnUnknownTaskFaultsRatherThanBeingDropped()
    {
        await _harness.Bus.Publish(new TaskStatusChanged("tracker", "TASK-1", "closed", Now), Ct);

        Assert.True(await _harness.Consumed.Any<TaskStatusChanged>(x => x.Exception is not null));
    }

    /// <summary>
    /// Crossing the closed boundary changes which merge requests are deployable, and reopening
    /// matters exactly as much as closing.
    /// </summary>
    [Theory]
    [InlineData("open", "closed")]
    [InlineData("closed", "open")]
    public async Task CrossingTheClosedBoundaryAsksForAReplan(string from, string to)
    {
        AddTask("TASK-1", status: from, closedAt: from == "closed" ? Now.AddDays(-1) : null);
        await _db.SaveChangesAsync(Ct);

        await _harness.Bus.Publish(new TaskStatusChanged("tracker", "TASK-1", to, Now), Ct);
        Assert.True(await _harness.Consumed.Any<TaskStatusChanged>());

        Assert.True(await _harness.Published.Any<ReleasePlanRecalculationRequested>());
    }

    /// <summary>
    /// A move between two open statuses changes nothing the plan can see, so replanning on it
    /// would rebuild the plan for every drag across a tracker board.
    /// </summary>
    [Fact]
    public async Task AMoveBetweenOpenStatusesAsksForNoReplan()
    {
        AddTask("TASK-1", status: "open");
        await _db.SaveChangesAsync(Ct);

        await _harness.Bus.Publish(new TaskStatusChanged("tracker", "TASK-1", "in progress", null), Ct);
        Assert.True(await _harness.Consumed.Any<TaskStatusChanged>());

        Assert.False(await _harness.Published.Any<ReleasePlanRecalculationRequested>());

        // The links are still re-read: the tracker raises no event when one changes, so any touch
        // of the issue is the cheapest moment to pull them.
        Assert.True(await _harness.Published.Any<TaskSyncRequested>());
    }
}
