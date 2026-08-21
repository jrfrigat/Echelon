using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Echelon.Application.Contracts.Messages;
using Echelon.Infrastructure.Persistence;
using Echelon.Infrastructure.Persistence.Models;
using Echelon.Infrastructure.Ingestion;
using Echelon.Infrastructure.Queue.Consumers;
using Xunit;

namespace Echelon.UnitTests.Queue;

/// <summary>
/// Branch reconciliation: what makes unlanded work visible to the launch guard.
/// </summary>
/// <remarks>
/// A snapshot is authoritative - present means the branch exists, absent means it is gone - so the
/// two directions that matter are that a vanished branch stops blocking and that a still-present one
/// keeps doing so. Runs over real SQLite, because the unique index on (RepositoryId, Name) is part of
/// what is being tested and an in-memory dictionary would not have it.
/// </remarks>
public sealed class BranchesObservedConsumerTests : IAsyncLifetime
{
    private static readonly DateTime Now = new(2026, 8, 1, 9, 0, 0, DateTimeKind.Utc);
    private static CancellationToken Ct => CancellationToken.None;

    /// <summary>The consumers report what arrived; these tests do not assert on it.</summary>
    private static IngestionActivity Activity => new(TimeProvider.System);

    private SqliteConnection _connection = null!;
    private AppDbContext _db = null!;
    private BranchesObservedConsumer _handler = null!;
    private Repository _repo = null!;

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        await _connection.OpenAsync(Ct);
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);
        await _db.Database.EnsureCreatedAsync(Ct);

        _handler = new BranchesObservedConsumer(
            _db, new FakeTimeProvider(Now), Activity, NullLogger<BranchesObservedConsumer>.Instance);

        var vcs = new VcsConnection
        {
            Id = Guid.NewGuid(),
            Name = "gitlab",
            ProviderType = "gitlab",
            ApiUrl = "https://gitlab.example.com"
        };
        _repo = new Repository
        {
            Id = Guid.NewGuid(),
            Name = "api",
            ExternalId = "group/api",
            ConnectionId = vcs.Id
        };
        _db.VcsConnections.Add(vcs);
        _db.Repositories.Add(_repo);
        await _db.SaveChangesAsync(Ct);
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        await _connection.DisposeAsync();
    }

    [Fact]
    public async Task StoresAnObservedBranchAndTheTaskItsNameNames()
    {
        await HandleAsync(new BranchesObserved.Branch("feature/PROJ-4-thing", IsMerged: false, IsDefault: false));

        var branch = Assert.Single(await _db.RepositoryBranches.ToListAsync(Ct));
        Assert.Equal("feature/PROJ-4-thing", branch.Name);
        Assert.Equal("PROJ-4", branch.TaskExternalId);
        Assert.False(branch.IsMerged);
    }

    [Fact]
    public async Task ARepeatedBranchNameInOneSnapshotDoesNotFailTheHandler()
    {
        // (RepositoryId, Name) is unique, so two inserts for one name would throw -- and under
        // at-least-once delivery that message redelivers and throws forever, taking every later
        // branch update for the repository with it. The last entry wins, matching the upsert.
        await HandleAsync(
            new BranchesObserved.Branch("feature/PROJ-4", IsMerged: false, IsDefault: false),
            new BranchesObserved.Branch("feature/PROJ-4", IsMerged: true, IsDefault: false));

        var branch = Assert.Single(await _db.RepositoryBranches.ToListAsync(Ct));
        Assert.True(branch.IsMerged);
    }

    [Fact]
    public async Task ABranchMissingFromTheSnapshotIsRemoved()
    {
        await HandleAsync(
            new BranchesObserved.Branch("feature/PROJ-4", IsMerged: false, IsDefault: false),
            new BranchesObserved.Branch("feature/PROJ-5", IsMerged: false, IsDefault: false));

        // PROJ-4 has been merged and deleted upstream; it must stop blocking anything.
        await HandleAsync(new BranchesObserved.Branch("feature/PROJ-5", IsMerged: false, IsDefault: false));

        var remaining = Assert.Single(await _db.RepositoryBranches.ToListAsync(Ct));
        Assert.Equal("feature/PROJ-5", remaining.Name);
    }

    [Fact]
    public async Task AnEmptySnapshotClearsEveryBranch()
    {
        await HandleAsync(new BranchesObserved.Branch("feature/PROJ-4", IsMerged: false, IsDefault: false));

        await HandleAsync();

        Assert.Empty(await _db.RepositoryBranches.ToListAsync(Ct));
    }

    [Fact]
    public async Task ReObservingABranchKeepsWhenItWasFirstSeen()
    {
        await HandleAsync(new BranchesObserved.Branch("feature/PROJ-4", IsMerged: false, IsDefault: false));
        var firstSeen = (await _db.RepositoryBranches.SingleAsync(Ct)).FirstSeenAt;

        await HandleAsync(new BranchesObserved.Branch("feature/PROJ-4", IsMerged: true, IsDefault: false));

        var branch = await _db.RepositoryBranches.SingleAsync(Ct);
        Assert.Equal(firstSeen, branch.FirstSeenAt);
        Assert.True(branch.IsMerged);
    }

    [Fact]
    public async Task ABranchWhoseNameNamesNoTaskIsStoredUnlinked()
    {
        // Stored, not dropped: it is still a branch. It simply blocks nothing, because it cannot be
        // attributed to any task's work.
        await HandleAsync(new BranchesObserved.Branch("main", IsMerged: false, IsDefault: true));

        var branch = Assert.Single(await _db.RepositoryBranches.ToListAsync(Ct));
        Assert.Null(branch.TaskExternalId);
        Assert.True(branch.IsDefault);
    }

    [Fact]
    public async Task AnUnknownRepositoryIsIgnoredRatherThanThrown()
    {
        // Not retryable: the repository is absent from configuration and redelivery will not
        // conjure it, so throwing would only produce a poison message.
        await _handler.Handle(new BranchesObserved(
            ConnectionName: "gitlab",
            RepositoryExternalId: "group/does-not-exist",
            Branches: [new BranchesObserved.Branch("feature/PROJ-4", false, false)],
            Source: "gitlab/gitlab",
            EventId: "e1"));

        Assert.Empty(await _db.RepositoryBranches.ToListAsync(Ct));
    }

    private Task HandleAsync(params BranchesObserved.Branch[] branches) =>
        _handler.Handle(new BranchesObserved(
            ConnectionName: "gitlab",
            RepositoryExternalId: _repo.ExternalId,
            Branches: branches,
            Source: "gitlab/gitlab",
            EventId: Guid.NewGuid().ToString("N")));
}
