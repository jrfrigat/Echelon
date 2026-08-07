using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ReleaseOrchestrator.Application.Auditing;
using ReleaseOrchestrator.Application.DTOs;
using ReleaseOrchestrator.Core.Enums;
using ReleaseOrchestrator.Infrastructure.Archive;
using ReleaseOrchestrator.Infrastructure.Audit;
using ReleaseOrchestrator.Infrastructure.Persistence;
using ReleaseOrchestrator.Infrastructure.Persistence.Models;
using Xunit;

namespace ReleaseOrchestrator.UnitTests.Audit;

/// <summary>
/// Covers the task timeline: what it includes, what it collapses, and what it refuses to claim.
/// </summary>
/// <remarks>
/// Run against a real (in-memory SQLite) database rather than hand-built lists, because the failure
/// mode being guarded is not arithmetic — it is a projection selecting the wrong set of rows and
/// producing a history that looks plausible. A list built in the test would encode the same mistake
/// the service makes.
///
/// NOT covered here: cascade ordering, provider-specific SQL, and the real middleware/controller
/// wiring. SQLite reproduces the query logic, not SQL Server's or PostgreSQL's execution.
/// </remarks>
public sealed class TaskTimelineServiceTests : IAsyncLifetime
{
    private static readonly DateTime Now = new(2026, 7, 20, 12, 0, 0, DateTimeKind.Utc);
    private static CancellationToken Ct => CancellationToken.None;

    private SqliteConnection _connection = null!;
    private SqliteConnection _archiveConnection = null!;
    private AppDbContext _db = null!;
    private ArchiveDbContext _archiveDb = null!;
    private TrackerConnection _tracker = null!;
    private VcsConnection _vcs = null!;
    private Repository _repo = null!;

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        await _connection.OpenAsync(Ct);
        _archiveConnection = new SqliteConnection("DataSource=:memory:");
        await _archiveConnection.OpenAsync(Ct);

        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);
        await _db.Database.EnsureCreatedAsync(Ct);
        _archiveDb = new ArchiveDbContext(
            new DbContextOptionsBuilder<ArchiveDbContext>().UseSqlite(_archiveConnection).Options);
        await _archiveDb.Database.EnsureCreatedAsync(Ct);

        _tracker = new TrackerConnection
        {
            Id = Guid.NewGuid(), Name = "tracker", ProviderType = "fake", ApiUrl = "https://tracker.example.com"
        };
        _vcs = new VcsConnection
        {
            Id = Guid.NewGuid(), Name = "vcs", ProviderType = "gitlab", ApiUrl = "https://gitlab.example.com"
        };
        _repo = new Repository
        {
            Id = Guid.NewGuid(), Name = "svc", ExternalId = "group/svc", ConnectionId = _vcs.Id
        };
        _db.TrackerConnections.Add(_tracker);
        _db.VcsConnections.Add(_vcs);
        _db.Repositories.Add(_repo);
        await _db.SaveChangesAsync(Ct);
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        await _archiveDb.DisposeAsync();
        await _connection.DisposeAsync();
        await _archiveConnection.DisposeAsync();
    }

    private TaskTimelineService Service() => new(_db, _archiveDb);

    private TaskItem AddTask(string key, DateTime? firstSeen = null, string? source = null)
    {
        var task = new TaskItem
        {
            Id = Guid.NewGuid(),
            ExternalId = key,
            Title = key,
            Status = "open",
            TrackerConnectionId = _tracker.Id,
            FirstSeenAt = firstSeen,
            FirstSeenSource = source
        };
        _db.Tasks.Add(task);
        return task;
    }

    private MergeRequest AddMr(TaskItem? task, string externalId, DateTime createdAt)
    {
        var mr = new MergeRequest
        {
            Id = Guid.NewGuid(),
            ExternalId = externalId,
            SourceBranch = "feature/x",
            TargetBranch = "main",
            RepositoryId = _repo.Id,
            TaskId = task?.Id,
            Status = MergeRequestStatus.Opened,
            CreatedAt = createdAt
        };
        _db.MergeRequests.Add(mr);
        return mr;
    }

    private int _planVersion;

    private RolloutPlan AddPlan(TaskItem task, DateTime at, string? hash, ActorRef? actor = null)
    {
        var plan = new RolloutPlan
        {
            Id = Guid.NewGuid(),
            TargetTaskId = task.Id,
            Version = ++_planVersion,
            Source = PlanSource.Generated,
            Status = PlanStatus.Ready,
            IsActive = false,
            CreatedAt = at,
            UpdatedAt = at,
            SnapshotStartedAt = at,
            ContentHash = hash,
            CreatedByOid = actor?.Oid,
            CreatedByKind = actor?.Kind,
            CreatedByName = actor?.DisplayName
        };
        _db.RolloutPlans.Add(plan);
        return plan;
    }

    // ---- what it reports ------------------------------------------------------

    [Fact]
    public async Task ReportsArrivalWhenRecorded_AndSaysSoWhenNot()
    {
        var recorded = AddTask("PROJ-1", Now.AddDays(-3), "tracker/prod");
        var legacy = AddTask("PROJ-2");
        await _db.SaveChangesAsync(Ct);

        var withArrival = await Service().GetAsync(recorded.Id, ct: Ct);
        Assert.Equal(Now.AddDays(-3), withArrival!.FirstSeenAt);
        Assert.Equal("tracker/prod", withArrival.FirstSeenSource);
        Assert.Contains(withArrival.Entries, e => e.Kind == TimelineKinds.TaskFirstSeen);

        // A task that predates the column must read as unknown, never as arriving today.
        var without = await Service().GetAsync(legacy.Id, ct: Ct);
        Assert.Null(without!.FirstSeenAt);
        Assert.DoesNotContain(without.Entries, e => e.Kind == TimelineKinds.TaskFirstSeen);
    }

    [Fact]
    public async Task ReportsWhoLaunchedWhereAndWhen()
    {
        var task = AddTask("PROJ-1");
        var env = new DeploymentEnvironment
        {
            Id = Guid.NewGuid(), Key = "prod", Name = "Production", Order = 1, IsEnabled = true
        };
        var plan = AddPlan(task, Now.AddHours(-2), "hash-a");
        _db.DeploymentEnvironments.Add(env);
        _db.Rollouts.Add(new Rollout
        {
            Id = Guid.NewGuid(),
            TargetTaskId = task.Id,
            RolloutPlanId = plan.Id,
            EnvironmentId = env.Id,
            PlanSnapshotJson = "{}",
            Status = RolloutStatus.Running,
            LaunchedByOid = "8f2c0000-0000-0000-0000-000000000a91",
            LaunchedByKind = ActorKinds.User,
            LaunchedByName = "vera@example.com",
            IdempotencyKey = "k",
            StartedAt = Now.AddHours(-1)
        });
        await _db.SaveChangesAsync(Ct);

        var timeline = await Service().GetAsync(task.Id, ct: Ct);

        var launch = Assert.Single(timeline!.Entries, e => e.Kind == RolloutEventKinds.Launched);
        Assert.Equal("prod", launch.SubjectKey);
        Assert.Equal(ActorKinds.User, launch.ActorKind);
        Assert.Equal("vera@example.com", launch.ActorName);
        Assert.Equal(Now.AddHours(-1), launch.At);
    }

    [Fact]
    public async Task ReportsRecordedStatusTransitionsWithTheirCauseAndActor()
    {
        var task = AddTask("PROJ-1");
        var mr = AddMr(task, "118", Now.AddDays(-1));
        await _db.SaveChangesAsync(Ct);

        MergeRequestStatusJournal.Record(
            _db, mr, MergeRequestStatus.Opened, MergeRequestStatus.ReadyForDeploy,
            MergeRequestStatusJournal.CauseManualPin,
            new ActorRef(ActorKinds.User, "8f2c0000-0000-0000-0000-000000000a91", "vera@example.com"),
            Now.AddHours(-5));
        await _db.SaveChangesAsync(Ct);

        var timeline = await Service().GetAsync(task.Id, ct: Ct);

        var change = Assert.Single(timeline!.Entries, e => e.Kind == TimelineKinds.MrStatusChanged);
        Assert.Equal(ActorKinds.User, change.ActorKind);
        Assert.Contains("ReadyForDeploy", change.Detail);
        Assert.Contains(MergeRequestStatusJournal.CauseManualPin, change.Detail);
    }

    /// <summary>
    /// The status column is overwritten in place, so a transition that was not journalled is gone.
    /// The timeline must not invent one from the current value.
    /// </summary>
    [Fact]
    public async Task DoesNotInventStatusTransitionsThatWereNeverRecorded()
    {
        var task = AddTask("PROJ-1");
        var mr = AddMr(task, "118", Now.AddDays(-1));
        mr.Status = MergeRequestStatus.ReadyForDeploy;
        await _db.SaveChangesAsync(Ct);

        var timeline = await Service().GetAsync(task.Id, ct: Ct);

        Assert.DoesNotContain(timeline!.Entries, e => e.Kind == TimelineKinds.MrStatusChanged);
    }

    // ---- the MR-union blocker -------------------------------------------------

    /// <summary>
    /// A merge request re-linked to another task keeps its history here, marked, rather than
    /// vanishing from this task and appearing on the other as though it had always been there.
    /// </summary>
    /// <remarks>
    /// MergeRequest.TaskId is rewritten on every webhook delivery. Selecting merge requests by the
    /// current value alone is the obvious implementation and it silently rewrites history.
    /// </remarks>
    [Fact]
    public async Task KeepsHistoryOfAMergeRequestThatWasLaterReassigned()
    {
        var original = AddTask("PROJ-1");
        var other = AddTask("PROJ-2");
        var mr = AddMr(original, "118", Now.AddDays(-2));
        await _db.SaveChangesAsync(Ct);

        // The plan recorded it while it belonged to PROJ-1.
        var plan = AddPlan(original, Now.AddDays(-2), "hash-a");
        var node = new PlanTaskNode { Id = Guid.NewGuid(), RolloutPlanId = plan.Id, TaskId = original.Id };
        _db.PlanTaskNodes.Add(node);
        _db.PlanItems.Add(new PlanItem { Id = Guid.NewGuid(), PlanTaskNodeId = node.Id, MergeRequestId = mr.Id });
        await _db.SaveChangesAsync(Ct);

        // A later delivery re-keys the branch onto the other task.
        mr.TaskId = other.Id;
        await _db.SaveChangesAsync(Ct);

        var timeline = await Service().GetAsync(original.Id, ct: Ct);

        var opened = Assert.Single(timeline!.Entries, e => e.Kind == TimelineKinds.MrOpened);
        Assert.True(opened.IsReassigned, "a merge request that moved away must be marked, not dropped");
    }

    // ---- plan collapse --------------------------------------------------------

    [Fact]
    public async Task CollapsesConsecutiveIdenticalMachineVersions()
    {
        var task = AddTask("PROJ-1");
        for (int i = 0; i < 5; i++)
            AddPlan(task, Now.AddMinutes(-50 + (i * 10)), "same-hash");
        await _db.SaveChangesAsync(Ct);

        var timeline = await Service().GetAsync(task.Id, ct: Ct);

        var plan = Assert.Single(timeline!.Entries, e => e.Kind == TimelineKinds.PlanRecalculated);
        Assert.Equal(5, plan.Repetitions);
        Assert.NotNull(plan.RepeatedUntil);
    }

    [Fact]
    public async Task DoesNotCollapseAcrossAContentChange()
    {
        var task = AddTask("PROJ-1");
        AddPlan(task, Now.AddMinutes(-30), "hash-a");
        AddPlan(task, Now.AddMinutes(-20), "hash-b");   // the real change
        AddPlan(task, Now.AddMinutes(-10), "hash-b");
        await _db.SaveChangesAsync(Ct);

        var timeline = await Service().GetAsync(task.Id, ct: Ct);

        var plans = timeline!.Entries.Where(e => e.Kind == TimelineKinds.PlanRecalculated).ToList();
        Assert.Equal(2, plans.Count);
    }

    /// <summary>
    /// A recalculation somebody asked for is shown even when it changed nothing: "I pressed it and
    /// nothing happened" is the answer to a real question.
    /// </summary>
    [Fact]
    public async Task NeverCollapsesAVersionAnOperatorAskedFor()
    {
        var task = AddTask("PROJ-1");
        var actor = new ActorRef(ActorKinds.User, "8f2c0000-0000-0000-0000-000000000a91", "vera@example.com");
        AddPlan(task, Now.AddMinutes(-30), "same-hash");
        AddPlan(task, Now.AddMinutes(-20), "same-hash", actor);
        AddPlan(task, Now.AddMinutes(-10), "same-hash");
        await _db.SaveChangesAsync(Ct);

        var timeline = await Service().GetAsync(task.Id, ct: Ct);

        var plans = timeline!.Entries.Where(e => e.Kind == TimelineKinds.PlanRecalculated).ToList();
        Assert.Equal(3, plans.Count);
        Assert.Single(plans, p => p.ActorKind == ActorKinds.User);
    }

    /// <summary>A version predating the hash column must not be grouped with anything.</summary>
    [Fact]
    public async Task NeverCollapsesVersionsWithNoContentHash()
    {
        var task = AddTask("PROJ-1");
        AddPlan(task, Now.AddMinutes(-30), hash: null);
        AddPlan(task, Now.AddMinutes(-20), hash: null);
        await _db.SaveChangesAsync(Ct);

        var timeline = await Service().GetAsync(task.Id, ct: Ct);

        Assert.Equal(2, timeline!.Entries.Count(e => e.Kind == TimelineKinds.PlanRecalculated));
    }

    // ---- ordering -------------------------------------------------------------

    /// <summary>
    /// Entries written in one unit of work share an exact instant, so a sort on the timestamp alone
    /// leaves their order to chance. Two identical reads must return identical sequences.
    /// </summary>
    [Fact]
    public async Task OrderingIsTotalAndStableUnderIdenticalTimestamps()
    {
        var task = AddTask("PROJ-1", Now, "tracker/prod");
        var sameInstant = Now.AddHours(-1);
        AddMr(task, "118", sameInstant);
        AddMr(task, "119", sameInstant);
        AddPlan(task, sameInstant, "hash-a");
        await _db.SaveChangesAsync(Ct);

        var first = await Service().GetAsync(task.Id, ct: Ct);
        var second = await Service().GetAsync(task.Id, ct: Ct);

        Assert.Equal(
            first!.Entries.Select(e => (e.At, e.Kind, e.SubjectKey)),
            second!.Entries.Select(e => (e.At, e.Kind, e.SubjectKey)));
        Assert.Equal(
            first.Entries.Select(e => e.At).OrderByDescending(a => a),
            first.Entries.Select(e => e.At));
    }

    // ---- honesty --------------------------------------------------------------

    [Fact]
    public async Task AnArchivedTaskAnswersWithItsHistoryRatherThanNothing()
    {
        var id = Guid.NewGuid();
        _archiveDb.ArchivedTasks.Add(new Infrastructure.Archive.Entities.ArchivedTask
        {
            Id = id,
            ExternalId = "PROJ-9",
            Title = "old",
            Status = "closed",
            ClosedAt = Now.AddDays(-100),
            FirstSeenAt = Now.AddDays(-200),
            FirstSeenSource = "tracker/prod",
            ArchivedAt = Now.AddDays(-1)
        });
        _archiveDb.ArchivedMergeRequests.Add(new Infrastructure.Archive.Entities.ArchivedMergeRequest
        {
            Id = Guid.NewGuid(),
            ExternalId = "77",
            RepositoryName = "svc",
            SourceBranch = "feature/x",
            TargetBranch = "main",
            Status = "Merged",
            TaskExternalId = "PROJ-9",
            CreatedAt = Now.AddDays(-190),
            MergedAt = Now.AddDays(-180),
            ArchivedAt = Now.AddDays(-1)
        });
        await _archiveDb.SaveChangesAsync(Ct);

        var timeline = await Service().GetAsync(id, ct: Ct);

        Assert.NotNull(timeline);
        Assert.True(timeline!.IsArchived);
        Assert.Equal(Now.AddDays(-200), timeline.FirstSeenAt);
        // The merge request's opening survived archiving only because CreatedAt is copied across.
        Assert.Contains(timeline.Entries, e => e.Kind == TimelineKinds.MrOpened);
        Assert.Contains(timeline.Entries, e => e.Kind == TimelineKinds.MrMerged);
    }

    [Fact]
    public async Task IsNullOnlyForATaskThatExistsNowhere() =>
        Assert.Null(await Service().GetAsync(Guid.NewGuid(), ct: Ct));

    /// <summary>
    /// Truncation must be reported even when no single source hit its cap.
    /// </summary>
    /// <remarks>
    /// The flag used to come only from the four capped queries, while merge requests and rollouts
    /// are uncapped by design. A task could therefore produce more entries than the limit with every
    /// individual source well under it, and the final cut discarded the oldest silently — leaving a
    /// history that begins mid-story next to a page that swears it is complete.
    /// </remarks>
    [Fact]
    public async Task ReportsTruncation_EvenWhenNoSingleSourceHitItsCap()
    {
        var task = AddTask("PROJ-1");
        // Four MrOpened entries from an uncapped source; nothing else contributes.
        for (var i = 0; i < 4; i++)
            AddMr(task, $"{100 + i}", Now.AddHours(-i));
        await _db.SaveChangesAsync(Ct);

        var timeline = await Service().GetAsync(task.Id, limit: 3, ct: Ct);

        Assert.Equal(3, timeline!.Entries.Count);
        Assert.True(timeline.Coverage.Truncated, "entries were dropped by the final cut and must be reported");
    }

    [Fact]
    public async Task ReportsTruncationRatherThanSilentlyDroppingEntries()
    {
        var task = AddTask("PROJ-1");
        for (int i = 0; i < 6; i++)
            AddPlan(task, Now.AddMinutes(-i), $"hash-{i}");
        await _db.SaveChangesAsync(Ct);

        var timeline = await Service().GetAsync(task.Id, limit: 3, ct: Ct);

        Assert.True(timeline!.Coverage.Truncated);
        Assert.Equal(3, timeline.Entries.Count);
    }

    /// <summary>
    /// Under the Local auth provider every token carries one configured object id, so attribution is
    /// decorative. The page must be able to say so instead of implying one colleague did everything.
    /// </summary>
    [Fact]
    public async Task FlagsSharedAttributionWhenEveryLaunchCarriesTheSameIdentity()
    {
        var task = AddTask("PROJ-1");
        var env = new DeploymentEnvironment
        {
            Id = Guid.NewGuid(), Key = "prod", Name = "Production", Order = 1, IsEnabled = true
        };
        var plan = AddPlan(task, Now.AddHours(-3), "hash-a");
        _db.DeploymentEnvironments.Add(env);

        for (int i = 0; i < 3; i++)
            _db.Rollouts.Add(new Rollout
            {
                Id = Guid.NewGuid(),
                TargetTaskId = task.Id,
                RolloutPlanId = plan.Id,
                EnvironmentId = env.Id,
                PlanSnapshotJson = "{}",
                Status = RolloutStatus.Succeeded,
                LaunchedByOid = "00000000-0000-0000-0000-0000000000ad",
                LaunchedByKind = ActorKinds.User,
                IdempotencyKey = $"k{i}",
                StartedAt = Now.AddHours(-i - 1)
            });
        await _db.SaveChangesAsync(Ct);

        var timeline = await Service().GetAsync(task.Id, ct: Ct);

        Assert.True(timeline!.Coverage.AttributionIsShared);
    }
}
