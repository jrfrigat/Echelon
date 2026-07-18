using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using ReleaseOrchestrator.Infrastructure.Persistence;
using ReleaseOrchestrator.Infrastructure.Queue;
using Xunit;

namespace ReleaseOrchestrator.UnitTests.Queue;

/// <summary>
/// Covers the dedup inbox: a first sighting is recorded (proceed), a repeat is dropped. Each call
/// uses a fresh <see cref="AppDbContext"/> on the same in-memory database, as the running service
/// does -- a new scope per message -- so a repeat is a database-level key conflict, not a tracked-key
/// clash within one context.
/// </summary>
public class ProcessedEventInboxTests : IAsyncLifetime
{
    private static readonly DateTime Now = new(2026, 7, 18, 12, 0, 0, DateTimeKind.Utc);

    private SqliteConnection _connection = null!;
    private DbContextOptions<AppDbContext> _options = null!;

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        await _connection.OpenAsync();
        _options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;

        await using var db = new AppDbContext(_options);
        await db.Database.EnsureCreatedAsync();
    }

    public async Task DisposeAsync() => await _connection.DisposeAsync();

    private ProcessedEventInbox Inbox() => new(new AppDbContext(_options), new FakeTimeProvider(Now));

    [Fact]
    public async Task FirstSighting_IsNew_RepeatIsDropped()
    {
        Assert.True(await Inbox().TryMarkProcessedAsync("gitlab/default", "evt-1", CancellationToken.None));
        Assert.False(await Inbox().TryMarkProcessedAsync("gitlab/default", "evt-1", CancellationToken.None));
    }

    [Fact]
    public async Task DifferentIdsAndSources_AreDistinct()
    {
        Assert.True(await Inbox().TryMarkProcessedAsync("gitlab/default", "evt-1", CancellationToken.None));
        Assert.True(await Inbox().TryMarkProcessedAsync("gitlab/default", "evt-2", CancellationToken.None));
        Assert.True(await Inbox().TryMarkProcessedAsync("gitlab/other", "evt-1", CancellationToken.None));

        await using var db = new AppDbContext(_options);
        Assert.Equal(3, await db.ProcessedEvents.CountAsync());
    }
}
