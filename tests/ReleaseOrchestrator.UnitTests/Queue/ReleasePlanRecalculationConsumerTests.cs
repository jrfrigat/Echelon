using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using ReleaseOrchestrator.Application.Contracts.Messages;
using ReleaseOrchestrator.Application.DTOs;
using ReleaseOrchestrator.Application.Services;
using ReleaseOrchestrator.Infrastructure.Persistence;
using ReleaseOrchestrator.Infrastructure.Persistence.Models;
using ReleaseOrchestrator.Infrastructure.Queue.Consumers;
using Xunit;

namespace ReleaseOrchestrator.UnitTests.Queue;

/// <summary>
/// The handler that rebuilds every active per-task plan when ingestion reports a change.
/// </summary>
/// <remarks>
/// The pivot retired the single global plan the old debounce-timer-then-consumer design rebuilt.
/// The claim worth pinning now is narrower: the handler rebuilds the plan of <em>every</em> task
/// with an active plan and of no others, and a failure propagates so Rebus redelivers rather than
/// dropping the recalculation. The planner is a recorder, not the real one — this asserts which
/// tasks the handler asks to rebuild, which is all its own logic decides; whether a rebuild is
/// correct is <see cref="ReleasePlanning.RolloutPlannerTests"/>' job.
/// </remarks>
public sealed class ReleasePlanRecalculationConsumerTests : IAsyncLifetime
{
    private static readonly DateTime Now = new(2026, 7, 17, 12, 0, 0, DateTimeKind.Utc);
    private static CancellationToken Ct => CancellationToken.None;

    private SqliteConnection _connection = null!;
    private AppDbContext _db = null!;
    private TrackerConnection _tracker = null!;
    private RecordingPlanner _planner = null!;
    private ReleasePlanRecalculationConsumer _handler = null!;

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

        _planner = new RecordingPlanner();
        _handler = new ReleasePlanRecalculationConsumer(_db, _planner, NullLogger<ReleasePlanRecalculationConsumer>.Instance);
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        await _connection.DisposeAsync();
    }

    /// <summary>Records the tasks it was asked to rebuild; the consumer ignores the result.</summary>
    private sealed class RecordingPlanner : IRolloutPlannerService
    {
        public List<Guid> Recalculated { get; } = [];
        public Exception? Throws { get; set; }

        public Task<RolloutPlanDto> RecalculateAsync(Guid taskId, CancellationToken ct = default)
        {
            Recalculated.Add(taskId);
            return Throws is not null
                ? Task.FromException<RolloutPlanDto>(Throws)
                : Task.FromResult<RolloutPlanDto>(null!);
        }

        public Task<IReadOnlyList<TaskListItemDto>> ListTasksAsync(int page, int pageSize, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<int> CountTasksAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<RolloutPlanDto?> GetActivePlanAsync(Guid taskId, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private async Task<Guid> AddTaskWithPlanAsync(bool active)
    {
        var task = new TaskItem
        {
            Id = Guid.NewGuid(),
            ExternalId = $"TASK-{Guid.NewGuid():N}",
            Title = "t",
            Status = "open",
            TrackerConnectionId = _tracker.Id
        };
        _db.Tasks.Add(task);
        _db.RolloutPlans.Add(new RolloutPlan
        {
            Id = Guid.NewGuid(),
            TargetTaskId = task.Id,
            Version = "1",
            IsActive = active,
            CreatedAt = Now,
            UpdatedAt = Now,
            SnapshotStartedAt = Now
        });
        await _db.SaveChangesAsync(Ct);
        return task.Id;
    }

    private Task HandleAsync() =>
        _handler.Handle(new ReleasePlanRecalculationRequested(Now, "test"));

    [Fact]
    public async Task RebuildsEveryActivePlansTaskAndNoOthers()
    {
        var active1 = await AddTaskWithPlanAsync(active: true);
        var active2 = await AddTaskWithPlanAsync(active: true);
        var inactiveTask = await AddTaskWithPlanAsync(active: false);

        await HandleAsync();

        Assert.Equal(2, _planner.Recalculated.Count);
        Assert.Contains(active1, _planner.Recalculated);
        Assert.Contains(active2, _planner.Recalculated);
        Assert.DoesNotContain(inactiveTask, _planner.Recalculated);
    }

    [Fact]
    public async Task WithNoActivePlansRebuildsNothing()
    {
        await AddTaskWithPlanAsync(active: false);

        await HandleAsync();

        Assert.Empty(_planner.Recalculated);
    }

    [Fact]
    public async Task AFailedRecalculationPropagatesSoTheMessageIsRedelivered()
    {
        await AddTaskWithPlanAsync(active: true);
        _planner.Throws = new InvalidOperationException("database is down");

        await Assert.ThrowsAsync<InvalidOperationException>(HandleAsync);
    }
}
