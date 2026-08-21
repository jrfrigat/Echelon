using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Echelon.Application.Contracts.Messages;
using Echelon.Infrastructure.Persistence;
using Echelon.Infrastructure.Persistence.Models;
using Echelon.Infrastructure.Ingestion;
using Echelon.Infrastructure.Queue.Consumers;
using Echelon.UnitTests.Tracker;
using Xunit;

namespace Echelon.UnitTests.Queue;

/// <summary>
/// How a task enters the system and how its status is applied.
/// </summary>
/// <remarks>
/// Both handlers forward TaskSyncRequested unconditionally, and that is the point rather than
/// noise: a tracker webhook carries the title and status but never the issue's links, and the
/// links are the only thing task dependencies are made of. Drop the request and the plan is
/// ordered by stack links alone - which is what the service did for its whole life. The two
/// handlers share one <see cref="RecordingBus"/>, so a test reads back whatever either forwarded.
/// </remarks>
public sealed class TaskConsumerTests : IAsyncLifetime
{
    private static readonly DateTime Now = new(2026, 7, 17, 12, 0, 0, DateTimeKind.Utc);
    private static CancellationToken Ct => CancellationToken.None;

    /// <summary>The consumers report what arrived; these tests do not assert on it.</summary>
    private static IngestionActivity Activity => new(TimeProvider.System);

    private SqliteConnection _connection = null!;
    private AppDbContext _db = null!;
    private RecordingBus _bus = null!;
    private TaskCreatedConsumer _created = null!;
    private TaskStatusChangedConsumer _statusChanged = null!;

    private TrackerConnection _tracker = null!;

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        await _connection.OpenAsync(Ct);
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);
        await _db.Database.EnsureCreatedAsync(Ct);

        _bus = new RecordingBus();
        _created = new TaskCreatedConsumer(_db, _bus, new FakeTimeProvider(Now), Activity, NullLogger<TaskCreatedConsumer>.Instance);
        _statusChanged = new TaskStatusChangedConsumer(
            _db,
            new FakeTrackerProviderFactory(new FakeTrackerProvider()),
            _bus,
            new FakeTimeProvider(Now),
            Activity, NullLogger<TaskStatusChangedConsumer>.Instance);

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
        await _created.Handle(new TaskCreated("tracker", "TASK-1", "Do the thing"));

        var task = await FindAsync("TASK-1", _tracker.Id);
        Assert.NotNull(task);
        Assert.Equal("Do the thing", task.Title);
    }

    /// <summary>
    /// At-least-once delivery means the same created event arrives twice. Inserting again violates
    /// the natural key; the handler upserts, so a redelivery refreshes the title instead.
    /// </summary>
    [Fact]
    public async Task ARedeliveredCreationUpdatesRatherThanDuplicates()
    {
        await _created.Handle(new TaskCreated("tracker", "TASK-1", "First title"));
        await _created.Handle(new TaskCreated("tracker", "TASK-1", "Renamed"));

        var task = Assert.Single(await _db.Tasks.ToListAsync(Ct));
        Assert.Equal("Renamed", task.Title);
    }

    [Fact]
    public async Task AnUnknownTrackerConnectionStoresNothing()
    {
        var exception = await Record.ExceptionAsync(
            () => _created.Handle(new TaskCreated("never-configured", "TASK-1", "Do the thing")));

        Assert.Null(exception);
        Assert.Empty(await _db.Tasks.ToListAsync(Ct));
    }

    /// <summary>
    /// The links are not in the webhook, and they are the whole of a task dependency.
    /// </summary>
    [Fact]
    public async Task ACreatedTaskAsksForItsLinksToBePulled()
    {
        await _created.Handle(new TaskCreated("tracker", "TASK-1", "Do the thing"));

        Assert.True(_bus.AnySent<TaskSyncRequested>());
    }

    // ---- TaskStatusChanged ---------------------------------------------------

    [Fact]
    public async Task AStatusChangeIsApplied()
    {
        AddTask("TASK-1");
        await _db.SaveChangesAsync(Ct);

        await _statusChanged.Handle(new TaskStatusChanged("tracker", "TASK-1", "in progress", null));

        var task = await FindAsync("TASK-1", _tracker.Id);
        Assert.Equal("in progress", task!.Status);
        Assert.Null(task.ClosedAt);
    }

    /// <summary>
    /// Which statuses mean "closed" is the adapter's to know, and it is asked rather than told.
    /// The ingress and this handler used to keep their own lists, which disagreed about
    /// "resolved" - so a resolved task was closed by one path and open to the other, and stayed
    /// closed-but-unarchivable forever.
    /// </summary>
    [Fact]
    public async Task AClosedStatusIsRecognisedByTheProviderAndStamped()
    {
        AddTask("TASK-1");
        await _db.SaveChangesAsync(Ct);

        await _statusChanged.Handle(new TaskStatusChanged("tracker", "TASK-1", "resolved", Now.AddHours(-2)));

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

        await _statusChanged.Handle(new TaskStatusChanged("tracker", "TASK-1", "closed", null));

        Assert.Equal(Now, (await FindAsync("TASK-1", _tracker.Id))!.ClosedAt);
    }

    [Fact]
    public async Task ReopeningClearsTheCloseTime()
    {
        AddTask("TASK-1", status: "closed", closedAt: Now.AddDays(-1));
        await _db.SaveChangesAsync(Ct);

        await _statusChanged.Handle(new TaskStatusChanged("tracker", "TASK-1", "open", null));

        Assert.Null((await FindAsync("TASK-1", _tracker.Id))!.ClosedAt);
    }

    /// <summary>
    /// Issue keys are unique only within a tracker. Matching on the key alone would stamp one
    /// tracker's status onto another tracker's task.
    /// </summary>
    /// <remarks>
    /// A theory over which tracker is targeted, and that is the whole design of it. Asserting a
    /// single direction passes whenever the unscoped query happens to return the right row first,
    /// which is decided by insertion order rather than by the code under test - the first version
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

        await _statusChanged.Handle(new TaskStatusChanged(targetTracker, "TASK-1", "closed", Now));

        var targetId = targetTracker == "tracker" ? _tracker.Id : other.Id;
        var bystanderId = targetTracker == "tracker" ? other.Id : _tracker.Id;

        Assert.NotNull((await FindAsync("TASK-1", targetId))!.ClosedAt);
        Assert.Null((await FindAsync("TASK-1", bystanderId))!.ClosedAt);
    }

    /// <summary>
    /// The created event is likely still in flight; retry is what resolves the ordering gap, and
    /// throwing is what makes Rebus redeliver.
    /// </summary>
    [Fact]
    public async Task AnUnknownTaskThrowsRatherThanBeingDropped()
    {
        await Assert.ThrowsAsync<TaskNotYetKnownException>(
            () => _statusChanged.Handle(new TaskStatusChanged("tracker", "TASK-1", "closed", Now)));
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

        await _statusChanged.Handle(new TaskStatusChanged("tracker", "TASK-1", to, Now));

        Assert.True(_bus.AnySent<ReleasePlanRecalculationRequested>());
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

        await _statusChanged.Handle(new TaskStatusChanged("tracker", "TASK-1", "in progress", null));

        Assert.False(_bus.AnySent<ReleasePlanRecalculationRequested>());

        // The links are still re-read: the tracker raises no event when one changes, so any touch
        // of the issue is the cheapest moment to pull them.
        Assert.True(_bus.AnySent<TaskSyncRequested>());
    }
}
