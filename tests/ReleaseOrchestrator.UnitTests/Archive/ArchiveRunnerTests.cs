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
/// alone, and that a second pass over already-archived rows does not throw.
///
/// The FK itself was checked against a real SQL Server on 2026-07-17, by hand rather than by a
/// test here: deleting a merge request a plan still refers to fails with error 547, so the
/// Restrict that <see cref="MergeRequestStillReferencedByTheExecutionEngineIsNotArchived"/> relies on is real and not
/// merely modelled. That check is not automated — nothing in CI has a database — so it is a
/// point-in-time fact, not a guard.
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
    /// A merge request still referenced by the execution engine must stay: the per-task plan, the
    /// rollout history and the deployment state each point at it with Restrict. Here the reference
    /// is a deployment-state row; the merge-request phase skips it rather than FK-violating on the
    /// delete, exactly as the retired global plan's StageItem gate used to.
    /// </summary>
    [Fact]
    public async Task MergeRequestStillReferencedByTheExecutionEngineIsNotArchived()
    {
        var mr = await AddMergeRequestAsync(MergeRequestStatus.Merged, mergedAt: Cutoff.AddDays(-1));

        var environment = new DeploymentEnvironment
        {
            Id = Guid.NewGuid(),
            Key = "prod",
            Name = "Production",
            Order = 1,
            IsEnabled = true
        };
        _db.DeploymentEnvironments.Add(environment);
        _db.MrDeploymentStates.Add(new MrDeploymentState
        {
            MergeRequestId = mr.Id,
            EnvironmentId = environment.Id,
            State = DeploymentState.Deployed,
            UpdatedAt = Now
        });
        await _db.SaveChangesAsync(Ct);

        await Runner().ArchiveMergeRequestsAsync(Cutoff, Ct);

        Assert.Single(await _db.MergeRequests.ToListAsync(Ct));
    }

    /// <summary>
    /// A closed task still referenced by the per-task plan or rollout history must stay: those point
    /// at TaskItem with Restrict (PlanTaskNode / RolloutPlan / Rollout / RolloutStep), and a
    /// prerequisite task can hold a plan node with no merge requests of its own -- so the merge-request
    /// and dependency gates do not cover it. Archiving it would FK-violate on delete and wedge the
    /// whole task batch, while leaving an orphan archive row.
    /// </summary>
    [Fact]
    public async Task TaskStillReferencedByARolloutPlanIsNotArchived()
    {
        var tracker = new TrackerConnection
        {
            Id = Guid.NewGuid(),
            Name = "tracker",
            ProviderType = "fake",
            ApiUrl = "https://tracker.example.com"
        };
        var task = new TaskItem
        {
            Id = Guid.NewGuid(),
            ExternalId = "PROJ-1",
            Title = "t",
            Status = "closed",
            ClosedAt = Cutoff.AddDays(-1),
            TrackerConnectionId = tracker.Id
        };
        _db.TrackerConnections.Add(tracker);
        _db.Tasks.Add(task);
        _db.RolloutPlans.Add(new RolloutPlan
        {
            Id = Guid.NewGuid(),
            TargetTaskId = task.Id,
            Version = "1",
            IsActive = true,
            CreatedAt = Now,
            UpdatedAt = Now,
            SnapshotStartedAt = Now
        });
        await _db.SaveChangesAsync(Ct);

        await Runner().ArchiveTasksAsync(Cutoff, Ct);

        Assert.Single(await _db.Tasks.ToListAsync(Ct));
        Assert.Empty(await _archiveDb.ArchivedTasks.ToListAsync(Ct));
    }

    /// <summary>
    /// A parent whose subtask is still here stays, and goes once the subtask does.
    /// </summary>
    /// <remarks>
    /// ParentTaskId is Restrict and self-referencing — SQL Server allows nothing else on a
    /// self-reference — so deleting a parent a child still points at FK-violates and wedges the whole
    /// task batch. It is also the same ordering constraint a dependency is: archiving the parent
    /// early would drop the edge out of a task still being planned. The child drains first, and the
    /// parent follows on a later pass, exactly as dependents drain before their prerequisites.
    /// </remarks>
    [Fact]
    public async Task ParentTaskIsNotArchivedWhileASubtaskStillPointsAtIt()
    {
        var tracker = new TrackerConnection
        {
            Id = Guid.NewGuid(),
            Name = "tracker",
            ProviderType = "fake",
            ApiUrl = "https://tracker.example.com"
        };
        var parent = new TaskItem
        {
            Id = Guid.NewGuid(),
            ExternalId = "EPIC-1",
            Title = "epic",
            Status = "closed",
            ClosedAt = Cutoff.AddDays(-1),
            TrackerConnectionId = tracker.Id
        };
        var child = new TaskItem
        {
            Id = Guid.NewGuid(),
            ExternalId = "PROJ-2",
            Title = "child",
            Status = "open",
            // Not closed, so nothing archives it: the parent has to wait regardless of its own age.
            ClosedAt = null,
            TrackerConnectionId = tracker.Id,
            ParentTaskId = parent.Id
        };
        _db.TrackerConnections.Add(tracker);
        _db.Tasks.AddRange(parent, child);
        await _db.SaveChangesAsync(Ct);

        await Runner().ArchiveTasksAsync(Cutoff, Ct);

        Assert.Equal(2, await _db.Tasks.CountAsync(Ct));
        Assert.Empty(await _archiveDb.ArchivedTasks.ToListAsync(Ct));

        // Once the subtask is gone, the parent is archivable on the next pass.
        _db.Tasks.Remove(child);
        await _db.SaveChangesAsync(Ct);

        await Runner().ArchiveTasksAsync(Cutoff, Ct);

        Assert.Empty(await _db.Tasks.ToListAsync(Ct));
        Assert.Single(await _archiveDb.ArchivedTasks.ToListAsync(Ct));
    }
}
