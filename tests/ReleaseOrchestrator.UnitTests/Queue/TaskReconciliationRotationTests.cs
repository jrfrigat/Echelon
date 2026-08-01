using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using ReleaseOrchestrator.Application.Contracts.Messages;
using ReleaseOrchestrator.Infrastructure.Persistence;
using ReleaseOrchestrator.Infrastructure.Persistence.Models;
using ReleaseOrchestrator.Infrastructure.Queue;
using Xunit;

namespace ReleaseOrchestrator.UnitTests.Queue;

/// <summary>
/// The reconciliation sweep must eventually cover every open task, not just the first page of them.
/// </summary>
/// <remarks>
/// This is the regression this file exists for. The pass used to take the first
/// <c>MaxTasksPerRun</c> tasks ordered by id on every run, so with more open tasks than the cap the
/// remainder was never swept at all — a dependency edge added in the tracker for one of them would
/// never arrive — while the log claimed the remainder was waiting for the next pass. Nothing failed
/// and nothing was logged; it simply did not happen.
/// </remarks>
public sealed class TaskReconciliationRotationTests : IAsyncLifetime
{
    private static readonly DateTime Now = new(2026, 8, 1, 9, 0, 0, DateTimeKind.Utc);
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

    [Fact]
    public async Task SuccessivePassesCoverEveryOpenTaskWhenThereAreMoreThanTheCap()
    {
        AddOpenTasks(10);
        await _db.SaveChangesAsync(Ct);

        var service = Service(cap: 4);
        var bus = new RecordingBus();

        // Three passes at a cap of four is enough for ten tasks only if each pass resumes where the
        // last stopped. Taking "the first four" every time would sweep four distinct tasks, forever.
        await service.ReconcileAsync(_db, bus, Ct);
        await service.ReconcileAsync(_db, bus, Ct);
        await service.ReconcileAsync(_db, bus, Ct);

        Assert.Equal(10, SyncedKeys(bus).Distinct().Count());
    }

    [Fact]
    public async Task APassResumesAfterThePreviousOneRatherThanRepeatingIt()
    {
        AddOpenTasks(6);
        await _db.SaveChangesAsync(Ct);

        var service = Service(cap: 3);
        var first = new RecordingBus();
        var second = new RecordingBus();

        await service.ReconcileAsync(_db, first, Ct);
        await service.ReconcileAsync(_db, second, Ct);

        Assert.Empty(SyncedKeys(first).Intersect(SyncedKeys(second)));
    }

    [Fact]
    public async Task TheRotationWrapsOnceItRunsOffTheEnd()
    {
        AddOpenTasks(4);
        await _db.SaveChangesAsync(Ct);

        var service = Service(cap: 3);
        var bus = new RecordingBus();

        await service.ReconcileAsync(_db, bus, Ct);   // 3 of 4
        await service.ReconcileAsync(_db, bus, Ct);   // the last 1
        var afterFullLap = SyncedKeys(bus).Count;

        // The cursor is now past every task. The next pass must start over rather than find nothing
        // and quietly stop reconciling for the rest of the process's life.
        await service.ReconcileAsync(_db, bus, Ct);

        Assert.Equal(4, afterFullLap);
        Assert.True(SyncedKeys(bus).Count > afterFullLap, "the pass after a full lap swept nothing");
    }

    [Fact]
    public async Task ClosedTasksAreNeverSwept()
    {
        AddOpenTasks(2);
        _db.Tasks.Add(new TaskItem
        {
            Id = Guid.NewGuid(),
            ExternalId = "CLOSED-1",
            Title = "done",
            Status = "closed",
            TrackerConnectionId = _tracker.Id,
            ClosedAt = Now.AddDays(-1)
        });
        await _db.SaveChangesAsync(Ct);

        var bus = new RecordingBus();
        await Service(cap: 50).ReconcileAsync(_db, bus, Ct);

        Assert.DoesNotContain("CLOSED-1", SyncedKeys(bus));
    }

    private TaskReconciliationService Service(int cap) =>
        new(
            scopeFactory: null!,   // unused: the pass under test is handed its own db and bus
            Options.Create(new TaskReconciliationOptions { MaxTasksPerRun = cap }),
            lease: null!,          // unused for the same reason
            new FakeTimeProvider(Now),
            NullLogger<TaskReconciliationService>.Instance);

    private void AddOpenTasks(int count)
    {
        for (var i = 0; i < count; i++)
            _db.Tasks.Add(new TaskItem
            {
                Id = Guid.NewGuid(),
                ExternalId = $"PROJ-{i:00}",
                Title = $"task {i}",
                Status = "open",
                TrackerConnectionId = _tracker.Id
            });
    }

    private static List<string> SyncedKeys(RecordingBus bus) =>
        [.. bus.Sent.OfType<TaskSyncRequested>().Select(m => m.ExternalId)];
}
