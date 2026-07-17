using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using ReleaseOrchestrator.Core.Entities;
using ReleaseOrchestrator.Core.Enums;
using ReleaseOrchestrator.Infrastructure.Persistence;
using ReleaseOrchestrator.Infrastructure.ReleasePlanning;
using Xunit;

namespace ReleaseOrchestrator.UnitTests.ReleasePlanning;

/// <summary>
/// ReleasePlanGraphTests prove the algorithm orders correctly given edges. These prove the planner
/// hands it the right edges and stores the answer — the half that was never covered, and the half
/// that actually broke: the algorithm was right the whole time while the data never arrived.
/// </summary>
/// <remarks>
/// The Include chain in RecalculateAsync is the load-bearing part. Drop one ThenInclude and the
/// navigation is simply empty: no exception, no warning, a plan that is wrong and looks fine. That
/// is invisible to a test that feeds the graph an in-memory list, which is why these go through a
/// database.
/// </remarks>
public sealed class ReleasePlannerTests : IAsyncLifetime
{
    private static readonly DateTime Now = new(2026, 7, 17, 12, 0, 0, DateTimeKind.Utc);
    private static CancellationToken Ct => CancellationToken.None;

    private SqliteConnection _connection = null!;
    private AppDbContext _db = null!;
    private TrackerConnection _tracker = null!;
    private VcsConnection _vcs = null!;

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
        _vcs = new VcsConnection
        {
            Id = Guid.NewGuid(),
            Name = "vcs",
            ProviderType = "gitlab",
            ApiUrl = "https://gitlab.example.com"
        };
        _db.TrackerConnections.Add(_tracker);
        _db.VcsConnections.Add(_vcs);
        await _db.SaveChangesAsync(Ct);
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        await _connection.DisposeAsync();
    }

    private ReleasePlanner Planner() =>
        new(_db, new FakeTimeProvider(Now), NullLogger<ReleasePlanner>.Instance);

    private Repository AddRepository(string name)
    {
        var repo = new Repository
        {
            Id = Guid.NewGuid(),
            Name = name,
            ExternalId = $"group/{name}",
            ConnectionId = _vcs.Id
        };
        _db.Repositories.Add(repo);
        return repo;
    }

    private TaskItem AddTask(string externalId)
    {
        var task = new TaskItem
        {
            Id = Guid.NewGuid(),
            ExternalId = externalId,
            Title = externalId,
            Status = "open",
            TrackerConnectionId = _tracker.Id
        };
        _db.Tasks.Add(task);
        return task;
    }

    /// <summary>Records that <paramref name="dependent"/> waits on <paramref name="dependsOn"/>.</summary>
    private void AddTaskDependency(TaskItem dependent, TaskItem dependsOn) =>
        _db.TaskDependencies.Add(new TaskDependency
        {
            Id = Guid.NewGuid(),
            DependentTaskId = dependent.Id,
            DependsOnTaskId = dependsOn.Id
        });

    private Stack AddStack(string name, params Repository[] repositories)
    {
        var stack = new Stack { Id = Guid.NewGuid(), Name = name };
        _db.Stacks.Add(stack);
        foreach (var repo in repositories)
            _db.Set<RepositoryStack>().Add(new RepositoryStack { RepositoryId = repo.Id, StackId = stack.Id });
        return stack;
    }

    /// <summary>Records that <paramref name="from"/> deploys after <paramref name="to"/>.</summary>
    private void AddStackDependency(Stack from, Stack to, StackDependencyType type) =>
        _db.Set<StackDependency>().Add(new StackDependency
        {
            Id = Guid.NewGuid(),
            FromStackId = from.Id,
            ToStackId = to.Id,
            Type = type
        });

    private MergeRequest AddMergeRequest(
        Repository repository,
        TaskItem? task = null,
        MergeRequestStatus status = MergeRequestStatus.ReadyForDeploy,
        DateTime? createdAt = null)
    {
        var mr = new MergeRequest
        {
            Id = Guid.NewGuid(),
            ExternalId = $"{_db.MergeRequests.Local.Count + 1}",
            SourceBranch = task is null ? "feature/x" : $"feature/{task.ExternalId}",
            TargetBranch = "main",
            RepositoryId = repository.Id,
            TaskId = task?.Id,
            Status = status,
            CreatedAt = createdAt ?? Now.AddDays(-1)
        };
        _db.MergeRequests.Add(mr);
        return mr;
    }

    private static List<Guid> StageItems(Application.DTOs.ReleasePlanDto plan, int sequence) =>
        plan.Stages.Single(s => s.Sequence == sequence).Items.Select(i => i.MergeRequestId).ToList();

    [Fact]
    public async Task OnlyReadyForDeployMergeRequestsEnterThePlan()
    {
        var repo = AddRepository("api");
        var ready = AddMergeRequest(repo);
        AddMergeRequest(repo, status: MergeRequestStatus.Opened);
        AddMergeRequest(repo, status: MergeRequestStatus.Reviewed);
        AddMergeRequest(repo, status: MergeRequestStatus.Merged);
        await _db.SaveChangesAsync(Ct);

        var plan = await Planner().RecalculateAsync(Ct);

        var item = Assert.Single(plan.Stages.SelectMany(s => s.Items));
        Assert.Equal(ready.Id, item.MergeRequestId);
    }

    /// <summary>
    /// The point of the whole service: a task edge has to reach the graph. It travels
    /// MergeRequest → Task → Dependencies, and if that Include is missing the collection is empty
    /// and both merge requests land in stage 1 — a plan that deploys them in the wrong order and
    /// reports nothing wrong.
    /// </summary>
    [Fact]
    public async Task ATaskDependencyPutsThePrerequisiteInAnEarlierStage()
    {
        var repo = AddRepository("api");
        var first = AddTask("TASK-1");
        var second = AddTask("TASK-2");
        AddTaskDependency(dependent: second, dependsOn: first);

        var firstMr = AddMergeRequest(repo, first);
        var secondMr = AddMergeRequest(repo, second);
        await _db.SaveChangesAsync(Ct);

        var plan = await Planner().RecalculateAsync(Ct);

        Assert.Equal(2, plan.Stages.Count);
        Assert.Equal([firstMr.Id], StageItems(plan, 1));
        Assert.Equal([secondMr.Id], StageItems(plan, 2));
        Assert.Empty(plan.Conflicts);
    }

    /// <summary>
    /// The stack edge travels the longest Include chain here — Repository → RepositoryStacks →
    /// Stack → DependentOn — which is the one most likely to be broken by a refactor.
    /// </summary>
    [Fact]
    public async Task AHardStackDependencyPutsTheRequiredStackFirst()
    {
        var database = AddRepository("db");
        var backend = AddRepository("backend");
        var databaseStack = AddStack("database", database);
        var backendStack = AddStack("backend", backend);
        AddStackDependency(from: backendStack, to: databaseStack, StackDependencyType.Hard);

        var databaseMr = AddMergeRequest(database);
        var backendMr = AddMergeRequest(backend);
        await _db.SaveChangesAsync(Ct);

        var plan = await Planner().RecalculateAsync(Ct);

        Assert.Equal([databaseMr.Id], StageItems(plan, 1));
        Assert.Equal([backendMr.Id], StageItems(plan, 2));
    }

    [Fact]
    public async Task UnrelatedMergeRequestsDeployTogetherInOneStage()
    {
        var api = AddRepository("api");
        var web = AddRepository("web");
        AddMergeRequest(api);
        AddMergeRequest(web);
        await _db.SaveChangesAsync(Ct);

        var plan = await Planner().RecalculateAsync(Ct);

        var stage = Assert.Single(plan.Stages);
        Assert.Equal(2, stage.Items.Count);
    }

    /// <summary>
    /// Two active plans and the deploy order depends on which one the reader happens to fetch. The
    /// database enforces this with a filtered unique index; this covers the planner holding up its
    /// end, since a failed deactivation surfaces here as a constraint violation.
    /// </summary>
    [Fact]
    public async Task RecalculatingLeavesExactlyOneActivePlan()
    {
        var repo = AddRepository("api");
        AddMergeRequest(repo);
        await _db.SaveChangesAsync(Ct);

        var planner = Planner();
        var first = await planner.RecalculateAsync(Ct);
        var second = await planner.RecalculateAsync(Ct);

        Assert.NotEqual(first.Id, second.Id);
        var active = Assert.Single(await _db.ReleasePlans.Where(p => p.IsActive).ToListAsync(Ct));
        Assert.Equal(second.Id, active.Id);
        Assert.Equal(2, await _db.ReleasePlans.CountAsync(Ct));
    }

    /// <summary>
    /// An operator's imported plan outranks an automatic one (README §6.1). Recalculation runs on a
    /// timer, so overwriting here would silently discard deliberate manual work minutes after it
    /// was saved.
    /// </summary>
    [Fact]
    public async Task AnActiveManualPlanSurvivesRecalculationAndTheNewPlanIsStoredInactive()
    {
        var repo = AddRepository("api");
        AddMergeRequest(repo);
        var manual = new ReleasePlan
        {
            Id = Guid.NewGuid(),
            Name = "imported",
            Version = "1",
            IsActive = true,
            AutoGenerated = false,
            CreatedAt = Now.AddHours(-1),
            UpdatedAt = Now.AddHours(-1),
            SnapshotStartedAt = Now.AddHours(-1)
        };
        _db.ReleasePlans.Add(manual);
        await _db.SaveChangesAsync(Ct);

        var recalculated = await Planner().RecalculateAsync(Ct);

        Assert.False(recalculated.IsActive);
        var active = Assert.Single(await _db.ReleasePlans.Where(p => p.IsActive).ToListAsync(Ct));
        Assert.Equal(manual.Id, active.Id);
    }

    /// <summary>
    /// A dropped constraint must reach the operator. Storing the plan without its conflicts gives
    /// an ordering that quietly violates a declared dependency and looks identical to a clean one.
    /// </summary>
    [Fact]
    public async Task ADroppedConstraintIsStoredOnThePlanAndReadBack()
    {
        var repo = AddRepository("api");
        var first = AddTask("TASK-1");
        var second = AddTask("TASK-2");
        AddTaskDependency(dependent: second, dependsOn: first);
        AddTaskDependency(dependent: first, dependsOn: second);

        AddMergeRequest(repo, first);
        AddMergeRequest(repo, second);
        await _db.SaveChangesAsync(Ct);

        var plan = await Planner().RecalculateAsync(Ct);

        var conflict = Assert.Single(plan.Conflicts);
        Assert.Equal(nameof(Application.ReleasePlanning.PlanEdgeKind.TaskDependency), conflict.Kind);

        // Still ordered: an operator gets a usable plan plus the reason it is imperfect.
        Assert.Equal(2, plan.Stages.Count);

        var reloaded = await Planner().GetByIdAsync(plan.Id, Ct);
        Assert.Single(reloaded!.Conflicts);
    }

    /// <summary>
    /// SnapshotStartedAt is stamped before the read, never after: a merge request committed while
    /// the query runs is not in the plan, and a plan claiming to be newer than it would let the
    /// debouncer skip the recalculation that would have included it.
    /// </summary>
    [Fact]
    public async Task APlanIsNotCurrentForAChangeThatArrivedAfterItsSnapshotBegan()
    {
        var repo = AddRepository("api");
        AddMergeRequest(repo);
        await _db.SaveChangesAsync(Ct);

        var planner = Planner();
        await planner.RecalculateAsync(Ct);

        Assert.True(await planner.IsPlanCurrentAsync(Now.AddSeconds(-1), Ct));
        Assert.False(await planner.IsPlanCurrentAsync(Now.AddSeconds(1), Ct));
    }

    /// <summary>
    /// The index is the actual guarantee — the planner deactivating first is only cooperation, and
    /// two concurrent recalculations both deactivate then both insert. Asserting it here proves the
    /// filter is real rather than decorative.
    /// </summary>
    [Fact]
    public async Task TheDatabaseRefusesASecondActivePlan()
    {
        async Task AddActivePlanAsync(string name)
        {
            _db.ReleasePlans.Add(new ReleasePlan
            {
                Id = Guid.NewGuid(),
                Name = name,
                Version = name,
                IsActive = true,
                AutoGenerated = true,
                CreatedAt = Now,
                UpdatedAt = Now,
                SnapshotStartedAt = Now
            });
            await _db.SaveChangesAsync(Ct);
        }

        await AddActivePlanAsync("first");

        await Assert.ThrowsAsync<DbUpdateException>(() => AddActivePlanAsync("second"));
    }

    [Fact]
    public async Task AnEmptyPlanIsStillStoredAndActive()
    {
        var plan = await Planner().RecalculateAsync(Ct);

        Assert.Empty(plan.Stages);
        Assert.True(plan.IsActive);
        Assert.NotNull(await _db.ReleasePlans.FindAsync([plan.Id], Ct));
    }
}
