using MassTransit;
using MassTransit.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using ReleaseOrchestrator.Application.Contracts.Messages;
using ReleaseOrchestrator.Application.Services;
using ReleaseOrchestrator.Core.Entities;
using ReleaseOrchestrator.Infrastructure.Persistence;
using ReleaseOrchestrator.Infrastructure.Queue.Consumers;
using Xunit;

namespace ReleaseOrchestrator.UnitTests.Queue;

/// <summary>
/// TaskSyncConsumer is what finally calls TrackerService — the call that never existed — and what
/// attaches merge requests that arrived before their task. Both are claims the documentation makes
/// and nothing checked.
/// </summary>
/// <remarks>
/// Run over MassTransit's own in-memory harness rather than a hand-written IPublishEndpoint: the
/// interface has a dozen overloads, and stubbing them proves only that the stub was called. The
/// harness ships in the MassTransit package, so this needs no dependency the proxy would block.
/// </remarks>
public sealed class TaskSyncConsumerTests : IAsyncLifetime
{
    private static readonly DateTime Now = new(2026, 7, 17, 12, 0, 0, DateTimeKind.Utc);
    private static CancellationToken Ct => CancellationToken.None;

    private SqliteConnection _connection = null!;
    private AppDbContext _db = null!;
    private ServiceProvider _provider = null!;
    private ITestHarness _harness = null!;
    private RecordingTrackerService _tracker = null!;

    private TrackerConnection _trackerConnection = null!;
    private VcsConnection _vcs = null!;

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        await _connection.OpenAsync(Ct);
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);
        await _db.Database.EnsureCreatedAsync(Ct);

        _tracker = new RecordingTrackerService();

        _provider = new ServiceCollection()
            .AddSingleton(_db)
            .AddSingleton<ITrackerService>(_tracker)
            .AddSingleton<TimeProvider>(new FakeTimeProvider(Now))
            .AddLogging()
            .AddMassTransitTestHarness(x => x.AddConsumer<TaskSyncConsumer>())
            .BuildServiceProvider(true);

        _harness = _provider.GetRequiredService<ITestHarness>();
        await _harness.Start();

        _trackerConnection = new TrackerConnection
        {
            Id = Guid.NewGuid(),
            Name = "tracker",
            ProviderType = "fake",
            ApiUrl = "https://tracker.example.com"
        };
        _vcs = new VcsConnection
        {
            Id = Guid.NewGuid(),
            Name = "vcs",
            ProviderType = "gitlab",
            ApiUrl = "https://gitlab.example.com"
        };
        _db.TrackerConnections.Add(_trackerConnection);
        _db.VcsConnections.Add(_vcs);
        await _db.SaveChangesAsync(Ct);
    }

    public async Task DisposeAsync()
    {
        await _harness.Stop();
        await _provider.DisposeAsync();
        await _db.DisposeAsync();
        await _connection.DisposeAsync();
    }

    /// <summary>A tracker that reports whatever the test needs and remembers what it was asked.</summary>
    private sealed class RecordingTrackerService : ITrackerService
    {
        public bool DependenciesChanged { get; set; }
        public Exception? Throws { get; set; }
        public List<(Guid ConnectionId, string ExternalId)> Syncs { get; } = [];

        public Task<bool> SyncTaskAsync(Guid trackerConnectionId, string externalTaskId, CancellationToken ct = default)
        {
            Syncs.Add((trackerConnectionId, externalTaskId));
            return Throws is not null ? Task.FromException<bool>(Throws) : Task.FromResult(DependenciesChanged);
        }
    }

    private Repository AddRepository(string name, Guid? trackerConnectionId)
    {
        var repo = new Repository
        {
            Id = Guid.NewGuid(),
            Name = name,
            ExternalId = $"group/{name}",
            ConnectionId = _vcs.Id,
            TrackerConnectionId = trackerConnectionId
        };
        _db.Repositories.Add(repo);
        return repo;
    }

    private TaskItem AddTask(string externalId, Guid? trackerConnectionId = null)
    {
        var task = new TaskItem
        {
            Id = Guid.NewGuid(),
            ExternalId = externalId,
            Title = externalId,
            Status = "open",
            TrackerConnectionId = trackerConnectionId ?? _trackerConnection.Id
        };
        _db.Tasks.Add(task);
        return task;
    }

    private MergeRequest AddMergeRequest(Repository repository, string? taskExternalId, Guid? taskId = null)
    {
        var mr = new MergeRequest
        {
            Id = Guid.NewGuid(),
            ExternalId = $"{_db.MergeRequests.Local.Count + 1}",
            SourceBranch = $"feature/{taskExternalId ?? "x"}",
            TargetBranch = "main",
            RepositoryId = repository.Id,
            TaskExternalId = taskExternalId,
            TaskId = taskId,
            Status = Core.Enums.MergeRequestStatus.Opened,
            CreatedAt = Now.AddDays(-1)
        };
        _db.MergeRequests.Add(mr);
        return mr;
    }

    private Task ConsumeAsync(string taskKey = "TASK-1", string connectionName = "tracker") =>
        _harness.Bus.Publish(new TaskSyncRequested(connectionName, taskKey, "test"), Ct);

    private async Task<MergeRequest> ReloadAsync(MergeRequest mr) =>
        await _db.MergeRequests.AsNoTracking().FirstAsync(m => m.Id == mr.Id, Ct);

    /// <summary>
    /// Webhook order is not guaranteed and a merge request commonly arrives before its task. Once
    /// its own event is handled nothing revisits it, so without this the merge request stays
    /// unlinked for good — and an unlinked merge request carries no task edges into the plan.
    /// </summary>
    [Fact]
    public async Task AMergeRequestThatArrivedBeforeItsTaskIsLinkedWhenTheTaskShowsUp()
    {
        var repo = AddRepository("api", _trackerConnection.Id);
        var waiting = AddMergeRequest(repo, taskExternalId: "TASK-1");
        var task = AddTask("TASK-1");
        await _db.SaveChangesAsync(Ct);

        await ConsumeAsync("TASK-1");
        Assert.True(await _harness.Consumed.Any<TaskSyncRequested>());

        Assert.Equal(task.Id, (await ReloadAsync(waiting)).TaskId);
    }

    /// <summary>
    /// Issue keys are a per-tracker namespace, not a global one: two trackers can both have
    /// PROJ-1. Linking across them would attach a merge request to a task from a different system
    /// and order the deploy by a dependency that has nothing to do with it.
    /// </summary>
    [Fact]
    public async Task AMergeRequestInAnotherTrackersRepositoryIsNotCaptured()
    {
        var other = new TrackerConnection
        {
            Id = Guid.NewGuid(),
            Name = "other-tracker",
            ProviderType = "fake",
            ApiUrl = "https://other.example.com"
        };
        _db.TrackerConnections.Add(other);

        var foreign = AddRepository("foreign", other.Id);
        var foreignMr = AddMergeRequest(foreign, taskExternalId: "TASK-1");
        AddTask("TASK-1");
        await _db.SaveChangesAsync(Ct);

        await ConsumeAsync("TASK-1");
        Assert.True(await _harness.Consumed.Any<TaskSyncRequested>());

        Assert.Null((await ReloadAsync(foreignMr)).TaskId);
    }

    /// <summary>
    /// A repository with no tracker at all must not be swept in either — TrackerConnectionId is
    /// nullable, and NULL is not a match.
    /// </summary>
    [Fact]
    public async Task AMergeRequestInARepositoryWithNoTrackerIsNotCaptured()
    {
        var untracked = AddRepository("untracked", trackerConnectionId: null);
        var mr = AddMergeRequest(untracked, taskExternalId: "TASK-1");
        AddTask("TASK-1");
        await _db.SaveChangesAsync(Ct);

        await ConsumeAsync("TASK-1");
        Assert.True(await _harness.Consumed.Any<TaskSyncRequested>());

        Assert.Null((await ReloadAsync(mr)).TaskId);
    }

    [Fact]
    public async Task AlreadyLinkedMergeRequestsAreLeftAlone()
    {
        var repo = AddRepository("api", _trackerConnection.Id);
        var otherTask = AddTask("TASK-9");
        await _db.SaveChangesAsync(Ct);

        var linked = AddMergeRequest(repo, taskExternalId: "TASK-1", taskId: otherTask.Id);
        AddTask("TASK-1");
        await _db.SaveChangesAsync(Ct);

        await ConsumeAsync("TASK-1");
        Assert.True(await _harness.Consumed.Any<TaskSyncRequested>());

        Assert.Equal(otherTask.Id, (await ReloadAsync(linked)).TaskId);
    }

    /// <summary>
    /// Linking a merge request changes the plan's input even when no dependency edge moved, so the
    /// replan has to be asked for on either trigger — not only on the tracker reporting a change.
    /// </summary>
    [Fact]
    public async Task LinkingAMergeRequestAsksForARecalculationEvenWhenNoEdgeChanged()
    {
        _tracker.DependenciesChanged = false;
        var repo = AddRepository("api", _trackerConnection.Id);
        AddMergeRequest(repo, taskExternalId: "TASK-1");
        AddTask("TASK-1");
        await _db.SaveChangesAsync(Ct);

        await ConsumeAsync("TASK-1");

        Assert.True(await _harness.Published.Any<ReleasePlanRecalculationRequested>());
    }

    [Fact]
    public async Task AChangedDependencyEdgeAsksForARecalculation()
    {
        _tracker.DependenciesChanged = true;
        AddTask("TASK-1");
        await _db.SaveChangesAsync(Ct);

        await ConsumeAsync("TASK-1");

        Assert.True(await _harness.Published.Any<ReleasePlanRecalculationRequested>());
    }

    /// <summary>
    /// The reconciliation sweeps every open task every 30 minutes. Replanning on each of them
    /// regardless of whether anything moved would rebuild the plan continuously for nothing.
    /// </summary>
    [Fact]
    public async Task ASyncThatChangedNothingDoesNotAskForARecalculation()
    {
        _tracker.DependenciesChanged = false;
        AddTask("TASK-1");
        await _db.SaveChangesAsync(Ct);

        await ConsumeAsync("TASK-1");
        Assert.True(await _harness.Consumed.Any<TaskSyncRequested>());

        Assert.False(await _harness.Published.Any<ReleasePlanRecalculationRequested>());
    }

    /// <summary>
    /// A misconfigured connection name is not retryable — redelivering it forever only fills the
    /// queue. The tracker must not be called at all, since there is no connection to call it on.
    /// </summary>
    [Fact]
    public async Task AnUnknownTrackerConnectionIsDroppedRatherThanRetried()
    {
        await ConsumeAsync("TASK-1", connectionName: "does-not-exist");

        Assert.True(await _harness.Consumed.Any<TaskSyncRequested>(x => x.Exception is null));
        Assert.Empty(_tracker.Syncs);
        Assert.False(await _harness.Published.Any<ReleasePlanRecalculationRequested>());
    }

    /// <summary>
    /// A tracker outage is transient and must fault so the broker redelivers. Swallowing it drops
    /// a dependency edge permanently: nothing revisits a task whose sync silently succeeded.
    /// </summary>
    [Fact]
    public async Task ATrackerOutageFaultsSoTheMessageIsRedelivered()
    {
        _tracker.Throws = new HttpRequestException("tracker is down");
        AddTask("TASK-1");
        await _db.SaveChangesAsync(Ct);

        await ConsumeAsync("TASK-1");

        Assert.True(await _harness.Consumed.Any<TaskSyncRequested>(x => x.Exception is not null));
        Assert.False(await _harness.Published.Any<ReleasePlanRecalculationRequested>());
    }

    [Fact]
    public async Task TheSyncIsScopedToTheNamedConnection()
    {
        AddTask("TASK-1");
        await _db.SaveChangesAsync(Ct);

        await ConsumeAsync("TASK-1");
        Assert.True(await _harness.Consumed.Any<TaskSyncRequested>());

        var sync = Assert.Single(_tracker.Syncs);
        Assert.Equal(_trackerConnection.Id, sync.ConnectionId);
        Assert.Equal("TASK-1", sync.ExternalId);
    }
}
