using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Echelon.Infrastructure.Persistence;
using Echelon.Infrastructure.Queue;
using Xunit;

namespace Echelon.UnitTests.Queue;

/// <summary>
/// Covers the dedup inbox: an event is not processed until it is marked, marking is idempotent, and
/// distinct (source, id) pairs are independent. Each call uses a fresh <see cref="AppDbContext"/> on
/// the same in-memory database, as the running service does -- a new scope per message -- so a repeat
/// mark is a database-level key conflict, not a tracked-key clash within one context.
/// </summary>
public class ProcessedEventInboxTests : IAsyncLifetime
{
    private static readonly DateTime Now = new(2026, 7, 18, 12, 0, 0, DateTimeKind.Utc);
    private static CancellationToken Ct => CancellationToken.None;

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
    public async Task NotProcessedUntilMarked()
    {
        Assert.False(await Inbox().IsProcessedAsync("gitlab/default", "evt-1", Ct));

        await Inbox().MarkProcessedAsync("gitlab/default", "evt-1", Ct);

        Assert.True(await Inbox().IsProcessedAsync("gitlab/default", "evt-1", Ct));
    }

    [Fact]
    public async Task MarkingTheSameEventTwiceIsIdempotent()
    {
        await Inbox().MarkProcessedAsync("gitlab/default", "evt-1", Ct);
        // A concurrent second delivery marking the same event is benign, not an error.
        await Inbox().MarkProcessedAsync("gitlab/default", "evt-1", Ct);

        await using var db = new AppDbContext(_options);
        Assert.Equal(1, await db.ProcessedEvents.CountAsync());
    }

    [Fact]
    public async Task DifferentIdsAndSources_AreDistinct()
    {
        await Inbox().MarkProcessedAsync("gitlab/default", "evt-1", Ct);
        await Inbox().MarkProcessedAsync("gitlab/default", "evt-2", Ct);
        await Inbox().MarkProcessedAsync("gitlab/other", "evt-1", Ct);

        await using var db = new AppDbContext(_options);
        Assert.Equal(3, await db.ProcessedEvents.CountAsync());
        Assert.True(await Inbox().IsProcessedAsync("gitlab/other", "evt-1", Ct));
        Assert.False(await Inbox().IsProcessedAsync("gitlab/other", "evt-2", Ct));
    }
}
