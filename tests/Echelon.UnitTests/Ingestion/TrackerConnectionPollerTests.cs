using Echelon.Application.Contracts.Messages;
using Echelon.Infrastructure.Ingestion;
using Echelon.Infrastructure.Persistence;
using Echelon.Infrastructure.Persistence.Models;
using Echelon.Providers.Abstractions.Tracker;
using Echelon.UnitTests.Queue;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Echelon.UnitTests.Ingestion;

/// <summary>
/// A polled tracker connection has to be able to start from nothing.
/// </summary>
/// <remarks>
/// The poller used to sweep the open tasks in the local database and ask the tracker to re-read each
/// one. On a fresh install there are none, and a poll-mode connection receives no webhook, so nothing
/// ever created the first task: the sweep ran over an empty set on every tick, reported success, and
/// the connection did nothing at all for as long as it existed. These tests pin both halves of the fix
/// - the tracker is asked what is open, and what is already known is still re-read, so a task closed
/// while nobody was looking is still noticed.
/// </remarks>
public sealed class TrackerConnectionPollerTests : IAsyncLifetime
{
    private static CancellationToken Ct => CancellationToken.None;

    private SqliteConnection _connection = null!;
    private AppDbContext _db = null!;
    private TrackerConnection _tracker = null!;
    private RecordingBus _bus = null!;

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

        _bus = new RecordingBus();
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        await _connection.DisposeAsync();
    }

    [Fact]
    public async Task DiscoversOpenIssuesThatAreNotInTheDatabaseYet()
    {
        // The case that never worked: nothing is local, so the old poller had nothing to sweep at all.
        var result = await Poller(new SearchableTracker("TASK-1", "TASK-2")).PollAsync(_tracker, Ct);

        Assert.Equal(2, result.Emitted);
        Assert.Equal(2, result.Discovered);
        Assert.Null(result.Failure);

        var requested = _bus.AllSent<TaskSyncRequested>()
            .Select(m => m.ExternalId)
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();
        Assert.Equal(["TASK-1", "TASK-2"], requested);
    }

    [Fact]
    public async Task RequestsTheSyncAgainstTheConnectionByName()
    {
        await Poller(new SearchableTracker("TASK-1")).PollAsync(_tracker, Ct);

        var sent = _bus.SingleSent<TaskSyncRequested>();
        Assert.Equal(_tracker.Name, sent.TrackerConnectionName);
        Assert.Contains("poll", sent.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StillReReadsAKnownTaskTheTrackerNoLongerReportsOpen()
    {
        await AddTaskAsync("TASK-9");

        // The tracker has closed TASK-9, so it is absent from the search. Only re-reading it notices
        // that; a discovery-only sweep would leave the local copy open for good.
        var result = await Poller(new SearchableTracker("TASK-1")).PollAsync(_tracker, Ct);

        Assert.Equal(2, result.Emitted);
        Assert.Equal(1, result.Discovered);
        Assert.Contains("TASK-9", _bus.AllSent<TaskSyncRequested>().Select(m => m.ExternalId));
    }

    [Fact]
    public async Task DoesNotQueueTheSameTaskTwiceWhenBothHalvesReportIt()
    {
        await AddTaskAsync("TASK-1");

        var result = await Poller(new SearchableTracker("task-1")).PollAsync(_tracker, Ct);

        Assert.Equal(1, result.Emitted);
        Assert.Equal(0, result.Discovered);
    }

    [Fact]
    public async Task IgnoresTasksAlreadyClosedLocally()
    {
        await AddTaskAsync("TASK-8", closed: true);

        var result = await Poller(new SearchableTracker()).PollAsync(_tracker, Ct);

        Assert.Equal(0, result.Emitted);
        Assert.Empty(_bus.AllSent<TaskSyncRequested>());
    }

    [Fact]
    public async Task CapsOneSweepAtTheConfiguredMaximum()
    {
        await AddTaskAsync("TASK-1");

        var result = await Poller(new SearchableTracker("TASK-2", "TASK-3"), maxTasksPerRun: 2)
            .PollAsync(_tracker, Ct);

        // The cap covers the union, not each half: one known plus one discovered, not two of each.
        Assert.Equal(2, result.Emitted);
        Assert.Equal(1, result.Discovered);
    }

    [Fact]
    public async Task ReportsATrackerThatCannotBeSearchedAndStillReReadsWhatIsKnown()
    {
        await AddTaskAsync("TASK-7");

        var result = await Poller(new UnsearchableTracker("no queue configured")).PollAsync(_tracker, Ct);

        Assert.Equal("no queue configured", result.Failure);
        Assert.Equal(1, result.Emitted);
        Assert.Equal(0, result.Discovered);
        Assert.Equal("TASK-7", _bus.SingleSent<TaskSyncRequested>().ExternalId);
    }

    [Fact]
    public async Task AProviderThatCannotSearchAtAllIsNotAFailure()
    {
        await AddTaskAsync("TASK-6");

        // A webhook-mode tracker does not implement the search port. Re-reading what is known is the
        // whole job there, and reporting a failure would cry wolf on every sweep.
        var result = await Poller(new PlainTracker()).PollAsync(_tracker, Ct);

        Assert.Null(result.Failure);
        Assert.Equal(1, result.Emitted);
    }

    private TrackerConnectionPoller Poller(ITrackerProvider provider, int maxTasksPerRun = 500) =>
        new(new Tracker.FakeTrackerProviderFactory(provider),
            _db,
            _bus,
            Options.Create(new TrackerPollingOptions { MaxTasksPerRun = maxTasksPerRun }),
            NullLogger<TrackerConnectionPoller>.Instance);

    private async Task AddTaskAsync(string externalId, bool closed = false)
    {
        _db.Tasks.Add(new TaskItem
        {
            Id = Guid.NewGuid(),
            TrackerConnectionId = _tracker.Id,
            ExternalId = externalId,
            Title = externalId,
            Status = "open",
            ClosedAt = closed ? new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc) : null
        });
        await _db.SaveChangesAsync(Ct);
    }

    /// <summary>A tracker that can be searched, answering with the keys it was given.</summary>
    private sealed class SearchableTracker(params string[] openKeys) : ITrackerProvider, ITrackerIssueSource
    {
        public TrackerCapabilities Capabilities => TrackerCapabilities.None;

        public Task<TrackerIssue?> GetIssueAsync(string issueKey, CancellationToken ct) =>
            Task.FromResult<TrackerIssue?>(null);

        public bool IsClosedStatus(string? statusKey) => statusKey is "closed";

        public Task<IReadOnlyList<string>> ListOpenIssueKeysAsync(int limit, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<string>>([.. openKeys.Take(limit)]);
    }

    /// <summary>A tracker whose search fails: a missing setting, a rejected token, an outage.</summary>
    private sealed class UnsearchableTracker(string reason) : ITrackerProvider, ITrackerIssueSource
    {
        public TrackerCapabilities Capabilities => TrackerCapabilities.None;

        public Task<TrackerIssue?> GetIssueAsync(string issueKey, CancellationToken ct) =>
            Task.FromResult<TrackerIssue?>(null);

        public bool IsClosedStatus(string? statusKey) => statusKey is "closed";

        public Task<IReadOnlyList<string>> ListOpenIssueKeysAsync(int limit, CancellationToken ct) =>
            throw new InvalidOperationException(reason);
    }

    /// <summary>A tracker that does not implement the search port, as a webhook-only one would not.</summary>
    private sealed class PlainTracker : ITrackerProvider
    {
        public TrackerCapabilities Capabilities => TrackerCapabilities.None;

        public Task<TrackerIssue?> GetIssueAsync(string issueKey, CancellationToken ct) =>
            Task.FromResult<TrackerIssue?>(null);

        public bool IsClosedStatus(string? statusKey) => statusKey is "closed";
    }
}
