using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using ReleaseOrchestrator.Infrastructure.Persistence.Models;
using ReleaseOrchestrator.Core.Enums;
using ReleaseOrchestrator.Infrastructure.Archive;
using ReleaseOrchestrator.Infrastructure.Persistence;
using Xunit;

namespace ReleaseOrchestrator.UnitTests.Archive;

/// <summary>
/// Archiving is the one component here that deletes data, so its mistakes are the only
/// unrecoverable ones — and it had never completed a single cycle before the audit. These tests
/// run against SQLite rather than nothing, which is what the whole .NET 10 move made possible:
/// under EF Core 9 no provider was available offline at a matching version.
/// </summary>
/// <remarks>
/// SQLite is not SQL Server. It does not reproduce the FK behaviour that broke archiving in the
/// first place, so what these assert is the runner's own logic: what it selects, what it leaves
/// alone, and that a second pass over already-archived rows does not throw. The FK ordering itself
/// stays unverified until this runs against a real SQL Server.
/// </remarks>
public sealed class ArchiveRunnerTests : IAsyncLifetime
{
    private static readonly DateTime Now = new(2026, 7, 17, 2, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Cutoff = Now.AddDays(-90);

    /// <summary>xunit v2 has no ambient test cancellation; these tests are in-memory and fast.</summary>
    private static CancellationToken Ct => CancellationToken.None;

    private SqliteConnection _operational = null!;
    private SqliteConnection _archive = null!;
    private AppDbContext _db = null!;
    private ArchiveDbContext _archiveDb = null!;

    public async Task InitializeAsync()
    {
        // In-memory, but a real connection: EF closes and reopens as it pleases, and an in-memory
        // SQLite database vanishes with its last connection. Holding one open keeps the schema.
        _operational = new SqliteConnection("DataSource=:memory:");
        _archive = new SqliteConnection("DataSource=:memory:");
        await _operational.OpenAsync(Ct);
        await _archive.OpenAsync(Ct);

        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_operational).Options);
        _archiveDb = new ArchiveDbContext(new DbContextOptionsBuilder<ArchiveDbContext>().UseSqlite(_archive).Options);

        await _db.Database.EnsureCreatedAsync(Ct);
        await _archiveDb.Database.EnsureCreatedAsync(Ct);
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        await _archiveDb.DisposeAsync();
        await _operational.DisposeAsync();
        await _archive.DisposeAsync();
    }

    private ArchiveRunner Runner(ArchiveOptions? options = null) =>
        new(_db, _archiveDb, options ?? new ArchiveOptions(), new FakeTimeProvider(Now), NullLogger.Instance);

    private async Task<MergeRequest> AddMergeRequestAsync(
        MergeRequestStatus status, DateTime? mergedAt = null, DateTime? closedAt = null)
    {
        var connection = new VcsConnection
        {
            Id = Guid.NewGuid(),
            Name = $"conn-{Guid.NewGuid():N}",
            ProviderType = "gitlab",
            ApiUrl = "https://gitlab.example.com"
        };
        var repository = new Repository
        {
            Id = Guid.NewGuid(),
            Name = "repo",
            ExternalId = $"group/repo-{Guid.NewGuid():N}",
            ConnectionId = connection.Id
        };
        var mr = new MergeRequest
        {
            Id = Guid.NewGuid(),
            ExternalId = "1",
            SourceBranch = "feature/PROJ-1",
            TargetBranch = "main",
            RepositoryId = repository.Id,
            Status = status,
            CreatedAt = Now.AddDays(-200),
            MergedAt = mergedAt,
            ClosedAt = closedAt
        };

        _db.VcsConnections.Add(connection);
        _db.Repositories.Add(repository);
        _db.MergeRequests.Add(mr);
        await _db.SaveChangesAsync(Ct);
        return mr;
    }

    [Fact]
    public async Task MergedMergeRequestPastTheCutoffIsArchivedAndRemoved()
    {
        var mr = await AddMergeRequestAsync(MergeRequestStatus.Merged, mergedAt: Cutoff.AddDays(-1));

        await Runner().ArchiveMergeRequestsAsync(Cutoff, Ct);

        Assert.Empty(await _db.MergeRequests.ToListAsync(Ct));
        var archived = Assert.Single(await _archiveDb.ArchivedMergeRequests.ToListAsync(Ct));
        Assert.Equal(mr.Id, archived.Id);
        Assert.Equal(mr.MergedAt, archived.MergedAt);
    }

    /// <summary>
    /// A closed MR never gets a merge timestamp. Filtering on MergedAt alone meant `NULL &lt; cutoff`
    /// — which SQL answers with UNKNOWN — so closed merge requests were never archived at all.
    /// </summary>
    [Fact]
    public async Task ClosedMergeRequestIsArchivedOnItsOwnTimestamp()
    {
        await AddMergeRequestAsync(MergeRequestStatus.Closed, closedAt: Cutoff.AddDays(-1));

        await Runner().ArchiveMergeRequestsAsync(Cutoff, Ct);

        var archived = Assert.Single(await _archiveDb.ArchivedMergeRequests.ToListAsync(Ct));
        Assert.NotNull(archived.ClosedAt);
        Assert.Null(archived.MergedAt);
    }

    [Fact]
    public async Task RecentlyMergedMergeRequestIsLeftAlone()
    {
        await AddMergeRequestAsync(MergeRequestStatus.Merged, mergedAt: Now.AddDays(-1));

        await Runner().ArchiveMergeRequestsAsync(Cutoff, Ct);

        Assert.Single(await _db.MergeRequests.ToListAsync(Ct));
        Assert.Empty(await _archiveDb.ArchivedMergeRequests.ToListAsync(Ct));
    }

    [Fact]
    public async Task OpenMergeRequestIsNeverArchivedHoweverOld()
    {
        await AddMergeRequestAsync(MergeRequestStatus.ReadyForDeploy);

        await Runner().ArchiveMergeRequestsAsync(Cutoff, Ct);

        Assert.Single(await _db.MergeRequests.ToListAsync(Ct));
    }

    /// <summary>
    /// The archive and the operational database cannot share a transaction, so a delete that fails
    /// leaves rows already archived. The next cycle selects them again, and re-inserting the same
    /// primary keys used to throw — wedging archiving permanently.
    /// </summary>
    [Fact]
    public async Task ArchivingRowsThatAreAlreadyInTheArchiveDoesNotThrow()
    {
        var mr = await AddMergeRequestAsync(MergeRequestStatus.Merged, mergedAt: Cutoff.AddDays(-1));

        _archiveDb.ArchivedMergeRequests.Add(new Infrastructure.Archive.Entities.ArchivedMergeRequest
        {
            Id = mr.Id,
            ExternalId = mr.ExternalId,
            RepositoryName = "repo",
            SourceBranch = mr.SourceBranch,
            TargetBranch = mr.TargetBranch,
            Status = mr.Status.ToString(),
            MergedAt = mr.MergedAt,
            ArchivedAt = Now.AddDays(-1)
        });
        await _archiveDb.SaveChangesAsync(Ct);

        var exception = await Record.ExceptionAsync(
            () => Runner().ArchiveMergeRequestsAsync(Cutoff, Ct));

        Assert.Null(exception);
        Assert.Empty(await _db.MergeRequests.ToListAsync(Ct));
        Assert.Single(await _archiveDb.ArchivedMergeRequests.ToListAsync(Ct));
    }

    /// <summary>
    /// A merge request still referenced by any plan must stay: StageItem points at it with
    /// Restrict. Filtering only on *active* plans is what made every MR that ever entered any plan
    /// permanently undeletable, because nothing removed the old plans.
    /// </summary>
    [Fact]
    public async Task MergeRequestStillInAPlanIsNotArchived()
    {
        var mr = await AddMergeRequestAsync(MergeRequestStatus.Merged, mergedAt: Cutoff.AddDays(-1));

        var plan = new ReleasePlan
        {
            Id = Guid.NewGuid(),
            Name = "old plan",
            Version = "1",
            IsActive = false,
            AutoGenerated = true,
            CreatedAt = Now.AddDays(-200),
            UpdatedAt = Now.AddDays(-200),
            SnapshotStartedAt = Now.AddDays(-200)
        };
        var stage = new ReleaseStage { Id = Guid.NewGuid(), PlanId = plan.Id, Sequence = 1 };
        plan.Stages.Add(stage);
        stage.Items.Add(new StageItem { Id = Guid.NewGuid(), StageId = stage.Id, MergeRequestId = mr.Id });

        _db.ReleasePlans.Add(plan);
        await _db.SaveChangesAsync(Ct);

        await Runner().ArchiveMergeRequestsAsync(Cutoff, Ct);

        Assert.Single(await _db.MergeRequests.ToListAsync(Ct));
    }
}
