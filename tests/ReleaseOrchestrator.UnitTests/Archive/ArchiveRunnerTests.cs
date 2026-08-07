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
/// Restrict that <see cref="MergeRequestStillReferencedByAPlanIsNotArchived"/> relies on is real and not
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
    /// A merge request a plan item still refers to must stay: <c>PlanItem</c> points at it with
    /// Restrict, so the phase skips it rather than FK-violating on the delete — exactly as the
    /// retired global plan's StageItem gate used to.
    /// </summary>
    [Fact]
    public async Task MergeRequestStillReferencedByAPlanIsNotArchived()
    {
        var mr = await AddMergeRequestAsync(MergeRequestStatus.Merged, mergedAt: Cutoff.AddDays(-1));
        var task = await AddOpenTaskAsync();

        _db.RolloutPlans.Add(new RolloutPlan
        {
            Id = Guid.NewGuid(),
            TargetTaskId = task.Id,
            Version = 1,
            IsActive = true,
            CreatedAt = Now,
            UpdatedAt = Now,
            SnapshotStartedAt = Now,
            Nodes =
            [
                new PlanTaskNode
                {
                    Id = Guid.NewGuid(),
                    TaskId = task.Id,
                    Items = [new PlanItem { Id = Guid.NewGuid(), MergeRequestId = mr.Id, Wave = 1 }]
                }
            ]
        });
        await _db.SaveChangesAsync(Ct);

        await Runner().ArchiveMergeRequestsAsync(Cutoff, Ct);

        Assert.Single(await _db.MergeRequests.ToListAsync(Ct));
    }

    /// <summary>
    /// Per-environment deployment state goes WITH the merge request rather than blocking it.
    /// </summary>
    /// <remarks>
    /// This used to be a gate, and it was the wrong shape of one: nothing ever deletes a deployment
    /// state, so a merge request that had been deployed anywhere was never archivable — which is most
    /// of them, and the archive's whole purpose. "Deployed to prod" is a fact ABOUT this merge
    /// request; once the row is gone the fact has no subject. The gates that remain (plan item,
    /// rollout step) are what guarantee nothing live is still asking.
    /// </remarks>
    [Fact]
    public async Task DeploymentStateIsRemovedWithItsMergeRequest()
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

        Assert.Empty(await _db.MergeRequests.ToListAsync(Ct));
        Assert.Empty(await _db.MrDeploymentStates.ToListAsync(Ct));
        Assert.Single(await _archiveDb.ArchivedMergeRequests.ToListAsync(Ct));
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
            Version = 1,
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

    /// <summary>
    /// The label journal is foreign-key-free and grows by one row per label change on every merge
    /// request, forever — so, like the status journal, it must be pruned on its own retention window
    /// or it grows without bound. This asserts the pruner drops what is past the window and keeps
    /// what is inside it.
    /// </summary>
    [Fact]
    public async Task LabelJournalRowsPastRetentionArePrunedAndRecentOnesKept()
    {
        var options = new ArchiveOptions { StatusJournalRetentionDays = 90 };
        var retentionCutoff = Now.AddDays(-options.StatusJournalRetentionDays);

        _db.MergeRequestLabelChanges.AddRange(
            new MergeRequestLabelChange
            {
                Id = Guid.NewGuid(),
                MergeRequestId = Guid.NewGuid(),
                MergeRequestExternalId = "1",
                FromLabels = string.Empty,
                ToLabels = "ready-for-prod",
                Cause = "webhook",
                At = retentionCutoff.AddDays(-1)
            },
            new MergeRequestLabelChange
            {
                Id = Guid.NewGuid(),
                MergeRequestId = Guid.NewGuid(),
                MergeRequestExternalId = "2",
                FromLabels = string.Empty,
                ToLabels = "ready-for-test",
                Cause = "poll",
                At = retentionCutoff.AddDays(1)
            });
        await _db.SaveChangesAsync(Ct);

        await Runner(options).PruneLabelJournalAsync(Now, Ct);

        var remaining = Assert.Single(await _db.MergeRequestLabelChanges.ToListAsync(Ct));
        Assert.Equal("2", remaining.MergeRequestExternalId);
    }

    // ---- retention: the phases that let the archive drain at all ----------------------------

    /// <summary>
    /// A task that was deployed reaches the archive — after a full cycle, not before it.
    /// </summary>
    /// <remarks>
    /// The point of the retention phases. Rollout steps reference the merge request and the task with
    /// Restrict and nothing ever deleted them, so before this any task that had been deployed once was
    /// pinned in the operational database permanently: the archive re-examined and re-skipped it every
    /// night for the life of the installation, and the rollout tables grew without bound.
    ///
    /// Asserted as a full cycle in the real order, because the phases only work as a sequence: each
    /// unblocks the next, and running them the other way round archives nothing while looking fine.
    /// </remarks>
    [Fact]
    public async Task DeployedTaskIsArchivedOnceItsRolloutHistoryAges()
    {
        var (task, mr) = await AddDeployedTaskAsync(finishedAt: Now.AddDays(-800));
        var runner = Runner();

        // Before retention: pinned, exactly as it used to be forever.
        await runner.ArchiveMergeRequestsAsync(Cutoff, Ct);
        await runner.ArchiveTasksAsync(Cutoff, Ct);
        Assert.Single(await _db.Tasks.ToListAsync(Ct));

        await runner.PruneRolloutHistoryAsync(Now, Ct);
        await runner.PrunePlanHistoryAsync(Now, Cutoff, Ct);
        await runner.ArchiveMergeRequestsAsync(Cutoff, Ct);
        await runner.ArchiveTasksAsync(Cutoff, Ct);

        Assert.Empty(await _db.Tasks.ToListAsync(Ct));
        Assert.Empty(await _db.MergeRequests.ToListAsync(Ct));
        Assert.Empty(await _db.Rollouts.ToListAsync(Ct));
        Assert.Empty(await _db.RolloutSteps.ToListAsync(Ct));

        // Archived, not merely deleted: the history has to survive somewhere.
        Assert.Equal(task.ExternalId, (await _archiveDb.ArchivedTasks.SingleAsync(Ct)).ExternalId);
        Assert.Equal(mr.ExternalId, (await _archiveDb.ArchivedMergeRequests.SingleAsync(Ct)).ExternalId);
    }

    /// <summary>A rollout inside its retention window keeps its task where it is.</summary>
    [Fact]
    public async Task RecentRolloutHistoryIsKeptAndStillPinsItsTask()
    {
        await AddDeployedTaskAsync(finishedAt: Now.AddDays(-10));
        var runner = Runner();

        await runner.PruneRolloutHistoryAsync(Now, Ct);
        await runner.PrunePlanHistoryAsync(Now, Cutoff, Ct);
        await runner.ArchiveMergeRequestsAsync(Cutoff, Ct);
        await runner.ArchiveTasksAsync(Cutoff, Ct);

        Assert.Single(await _db.Rollouts.ToListAsync(Ct));
        Assert.Single(await _db.Tasks.ToListAsync(Ct));
    }

    /// <summary>
    /// A rollout that never finished is not history, however old the row is.
    /// </summary>
    /// <remarks>
    /// Its age says how long it has been stuck, not how long ago it ended — and deleting it would
    /// take away the steps that say where it stopped, which is the only reason anyone opens it.
    /// </remarks>
    [Fact]
    public async Task AnUnfinishedRolloutIsNeverPruned()
    {
        await AddDeployedTaskAsync(finishedAt: null, status: RolloutStatus.Running);

        await Runner().PruneRolloutHistoryAsync(Now, Ct);

        Assert.Single(await _db.Rollouts.ToListAsync(Ct));
    }

    /// <summary>Superseded versions age out on their own retention; the active one stays.</summary>
    [Fact]
    public async Task SupersededPlanVersionsArePrunedAndTheActiveOneIsKept()
    {
        var task = await AddOpenTaskAsync();
        AddPlan(task, version: 1, isActive: false, createdAt: Now.AddDays(-200));
        AddPlan(task, version: 2, isActive: false, createdAt: Now.AddDays(-10));
        AddPlan(task, version: 3, isActive: true, createdAt: Now);
        await _db.SaveChangesAsync(Ct);

        await Runner().PrunePlanHistoryAsync(Now, Cutoff, Ct);

        var remaining = await _db.RolloutPlans.OrderBy(p => p.Version).ToListAsync(Ct);
        Assert.Equal([2, 3], remaining.Select(p => p.Version));
    }

    /// <summary>
    /// The active plan of a long-closed task goes too, because otherwise the task never can.
    /// </summary>
    /// <remarks>
    /// The surprising half of the rule, and deliberate: PlanTaskNode pins the task with Restrict, so
    /// keeping the plan keeps the task. Rebuilding it is one Recalculate away — that is what a plan
    /// being a projection of the atlas buys.
    /// </remarks>
    [Fact]
    public async Task ActivePlanOfALongClosedTaskIsPruned()
    {
        var task = await AddOpenTaskAsync();
        task.Status = "closed";
        task.ClosedAt = Cutoff.AddDays(-1);
        AddPlan(task, version: 1, isActive: true, createdAt: Now);
        await _db.SaveChangesAsync(Ct);

        await Runner().PrunePlanHistoryAsync(Now, Cutoff, Ct);

        Assert.Empty(await _db.RolloutPlans.ToListAsync(Ct));
    }

    // ---- retention builders -----------------------------------------------------------------

    private async Task<TaskItem> AddOpenTaskAsync()
    {
        var tracker = new TrackerConnection
        {
            Id = Guid.NewGuid(),
            Name = $"tracker-{Guid.NewGuid():N}",
            ProviderType = "fake",
            ApiUrl = "https://tracker.example.com"
        };
        var task = new TaskItem
        {
            Id = Guid.NewGuid(),
            ExternalId = "PROJ-1",
            Title = "t",
            Status = "open",
            TrackerConnectionId = tracker.Id
        };

        _db.TrackerConnections.Add(tracker);
        _db.Tasks.Add(task);
        await _db.SaveChangesAsync(Ct);
        return task;
    }

    private void AddPlan(TaskItem task, int version, bool isActive, DateTime createdAt) =>
        _db.RolloutPlans.Add(new RolloutPlan
        {
            Id = Guid.NewGuid(),
            TargetTaskId = task.Id,
            Version = version,
            IsActive = isActive,
            CreatedAt = createdAt,
            UpdatedAt = createdAt,
            SnapshotStartedAt = createdAt
        });

    /// <summary>
    /// A closed task that was planned and deployed: a plan with its node and item, a rollout with a
    /// step, and per-environment deployment state — every Restrict that used to pin it.
    /// </summary>
    private async Task<(TaskItem Task, MergeRequest Mr)> AddDeployedTaskAsync(
        DateTime? finishedAt, RolloutStatus status = RolloutStatus.Succeeded)
    {
        var task = await AddOpenTaskAsync();
        task.Status = "closed";
        task.ClosedAt = Cutoff.AddDays(-1);

        var mr = await AddMergeRequestAsync(MergeRequestStatus.Merged, mergedAt: Cutoff.AddDays(-1));
        mr.TaskId = task.Id;

        var environment = new DeploymentEnvironment
        {
            Id = Guid.NewGuid(),
            Key = "prod",
            Name = "Production",
            Order = 1
        };
        _db.DeploymentEnvironments.Add(environment);

        var plan = new RolloutPlan
        {
            Id = Guid.NewGuid(),
            TargetTaskId = task.Id,
            Version = 1,
            IsActive = true,
            CreatedAt = Now.AddDays(-150),
            UpdatedAt = Now.AddDays(-150),
            SnapshotStartedAt = Now.AddDays(-150),
            Nodes =
            [
                new PlanTaskNode
                {
                    Id = Guid.NewGuid(),
                    TaskId = task.Id,
                    Items = [new PlanItem { Id = Guid.NewGuid(), MergeRequestId = mr.Id, Wave = 1 }]
                }
            ]
        };
        _db.RolloutPlans.Add(plan);

        var rollout = new Rollout
        {
            Id = Guid.NewGuid(),
            TargetTaskId = task.Id,
            EnvironmentId = environment.Id,
            RolloutPlanId = plan.Id,
            Status = status,
            IdempotencyKey = Guid.NewGuid().ToString("N"),
            StartedAt = Now.AddDays(-150),
            FinishedAt = finishedAt,
            Steps =
            [
                new RolloutStep
                {
                    Id = Guid.NewGuid(),
                    MergeRequestId = mr.Id,
                    TaskId = task.Id,
                    Wave = 1,
                    DeployStrategyKey = "merge",
                    State = RolloutStepState.Succeeded
                }
            ]
        };
        _db.Rollouts.Add(rollout);

        _db.MrDeploymentStates.Add(new MrDeploymentState
        {
            MergeRequestId = mr.Id,
            EnvironmentId = environment.Id,
            State = DeploymentState.Deployed,
            UpdatedAt = Now.AddDays(-150)
        });

        await _db.SaveChangesAsync(Ct);
        return (task, mr);
    }
}
