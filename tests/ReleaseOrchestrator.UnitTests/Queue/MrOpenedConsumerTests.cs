using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using ReleaseOrchestrator.Application.Contracts.Messages;
using ReleaseOrchestrator.Core.Enums;
using ReleaseOrchestrator.Infrastructure.Persistence;
using ReleaseOrchestrator.Infrastructure.Persistence.Models;
using ReleaseOrchestrator.Infrastructure.Queue.Consumers;
using Xunit;

namespace ReleaseOrchestrator.UnitTests.Queue;

/// <summary>
/// The front door: every merge request enters the system here, and what this handler decides —
/// deployable or not, linked to a task or not — is what the planner later orders.
/// </summary>
/// <remarks>
/// MergeRequestStatusResolverTests already cover the label rule itself. These cover what the
/// handler does around it: the upsert, the task resolution, and the replan request. It runs over a
/// real SQLite database with a <see cref="RecordingBus"/> capturing what it forwards, so "sent a
/// message" is observed rather than stubbed.
/// </remarks>
public sealed class MrOpenedConsumerTests : IAsyncLifetime
{
    private static readonly DateTime Now = new(2026, 7, 17, 12, 0, 0, DateTimeKind.Utc);
    private static CancellationToken Ct => CancellationToken.None;

    private const string ReadyLabel = "ready-for-deploy";

    private SqliteConnection _connection = null!;
    private AppDbContext _db = null!;
    private RecordingBus _bus = null!;
    private MrOpenedConsumer _handler = null!;

    private VcsConnection _vcs = null!;
    private TrackerConnection _tracker = null!;

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        await _connection.OpenAsync(Ct);
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);
        await _db.Database.EnsureCreatedAsync(Ct);

        _bus = new RecordingBus();
        _handler = new MrOpenedConsumer(_db, _bus, new FakeTimeProvider(Now), NullLogger<MrOpenedConsumer>.Instance);

        _vcs = new VcsConnection
        {
            Id = Guid.NewGuid(),
            Name = "gitlab",
            ProviderType = "gitlab",
            ApiUrl = "https://gitlab.example.com"
        };
        _tracker = new TrackerConnection
        {
            Id = Guid.NewGuid(),
            Name = "tracker",
            ProviderType = "fake",
            ApiUrl = "https://tracker.example.com"
        };
        _db.VcsConnections.Add(_vcs);
        _db.TrackerConnections.Add(_tracker);
        await _db.SaveChangesAsync(Ct);
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        await _connection.DisposeAsync();
    }

    [Fact]
    public async Task LinksFromTheTitleWhenTheConnectionConfiguresThatSource()
    {
        // The connection's rule reads the key from the title, so a branch with no key still links.
        _vcs.ProviderSettingsJson = """{"taskKeySource":"title"}""";
        AddRepository();
        AddTask("PROJ-9");
        await _db.SaveChangesAsync(Ct);

        await _handler.Handle(new MrOpened(
            "gitlab", "group/api", "1",
            SourceBranch: "feature/no-key-in-branch", TargetBranch: "main",
            Title: "PROJ-9: add the thing", Labels: []));

        var mr = await FindMrAsync();
        Assert.NotNull(mr);
        Assert.Equal("PROJ-9", mr!.TaskExternalId);
    }

    private Repository AddRepository(string name = "api", Guid? trackerConnectionId = null)
    {
        var repo = new Repository
        {
            Id = Guid.NewGuid(),
            Name = name,
            ExternalId = $"group/{name}",
            ConnectionId = _vcs.Id,
            TrackerConnectionId = trackerConnectionId
        };
        _db.Repositories.Add(repo);
        return repo;
    }

    private TaskItem AddTask(string externalId, Guid? trackerConnectionId = null)
    {
        var task = new TaskItem
        {
            Id = Guid.NewGuid(),
            ExternalId = externalId,
            Title = externalId,
            Status = "open",
            TrackerConnectionId = trackerConnectionId ?? _tracker.Id
        };
        _db.Tasks.Add(task);
        return task;
    }

    private Task OpenAsync(
        string repositoryExternalId = "group/api",
        string mrId = "1",
        string? taskExternalId = null,
        params string[] labels) =>
        // The key rides in the branch (the connection's default rule reads it from there); the
        // consumer resolves it, so the message carries no pre-resolved key. Title is unused here.
        _handler.Handle(
            new MrOpened("gitlab", repositoryExternalId, mrId, $"feature/{taskExternalId ?? "x"}", "main", Title: null, labels));

    private Task<MergeRequest?> FindMrAsync(string externalId = "1") =>
        _db.MergeRequests.AsNoTracking().FirstOrDefaultAsync(m => m.ExternalId == externalId, Ct);

    [Fact]
    public async Task AnUnknownRepositoryIsIgnoredRatherThanFaulted()
    {
        // Redelivering will not conjure a repository, so this must not throw — but it must also
        // not invent one.
        var exception = await Record.ExceptionAsync(() => OpenAsync(repositoryExternalId: "group/never-configured"));

        Assert.Null(exception);
        Assert.Empty(await _db.MergeRequests.ToListAsync(Ct));
    }

    [Fact]
    public async Task AnObservedLabelIsPersistedButDoesNotChangeStatus()
    {
        AddRepository();
        await _db.SaveChangesAsync(Ct);

        await OpenAsync(labels: ReadyLabel);

        var mr = (await FindMrAsync())!;
        // No coarse promotion any more: an open MR is Opened. The label is persisted so a readiness
        // rule can consult it (label:ready-for-deploy) at launch.
        Assert.Equal(MergeRequestStatus.Opened, mr.Status);
        Assert.Contains("ready-for-deploy", mr.Labels, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithoutTheLabelTheMergeRequestIsMerelyOpen()
    {
        AddRepository();
        await _db.SaveChangesAsync(Ct);

        await OpenAsync();

        Assert.Equal(MergeRequestStatus.Opened, (await FindMrAsync())!.Status);
    }

    /// <summary>
    /// GitLab re-sends "opened" on every label change and push, so the second event is the normal
    /// case, not the exception. Inserting again would break the natural key; ignoring it would
    /// freeze the merge request at whatever it looked like first.
    /// </summary>
    [Fact]
    public async Task ASecondEventUpdatesTheSameMergeRequest()
    {
        AddRepository();
        await _db.SaveChangesAsync(Ct);

        await OpenAsync();
        await OpenAsync(labels: ReadyLabel);

        var mr = Assert.Single(await _db.MergeRequests.ToListAsync(Ct));
        Assert.Equal(MergeRequestStatus.Opened, mr.Status);
        // The second event updated the same row: its label is now captured.
        Assert.Contains("ready-for-deploy", mr.Labels, StringComparison.Ordinal);
    }

    /// <summary>
    /// An operator's decision outranks a label: a webhook must not silently undo it. The resolver
    /// owns the rule; this checks the handler actually passes it the stored flag rather than a
    /// fresh default.
    /// </summary>
    [Fact]
    public async Task ALabelDoesNotOverrideAManualStatus()
    {
        var repo = AddRepository();
        _db.MergeRequests.Add(new MergeRequest
        {
            Id = Guid.NewGuid(),
            ExternalId = "1",
            SourceBranch = "feature/x",
            TargetBranch = "main",
            RepositoryId = repo.Id,
            Status = MergeRequestStatus.Reviewed,
            IsStatusManual = true,
            CreatedAt = Now.AddDays(-1)
        });
        await _db.SaveChangesAsync(Ct);

        await OpenAsync(labels: ReadyLabel);

        Assert.Equal(MergeRequestStatus.Reviewed, (await FindMrAsync())!.Status);
    }

    /// <summary>
    /// A reopened merge request that keeps its terminal timestamps is archived out from under the
    /// plan it just re-entered.
    /// </summary>
    [Fact]
    public async Task ReopeningShedsTheTerminalTimestamps()
    {
        var repo = AddRepository();
        _db.MergeRequests.Add(new MergeRequest
        {
            Id = Guid.NewGuid(),
            ExternalId = "1",
            SourceBranch = "feature/x",
            TargetBranch = "main",
            RepositoryId = repo.Id,
            Status = MergeRequestStatus.Closed,
            ClosedAt = Now.AddDays(-2),
            MergedAt = Now.AddDays(-3),
            CreatedAt = Now.AddDays(-10)
        });
        await _db.SaveChangesAsync(Ct);

        await OpenAsync();

        var mr = (await FindMrAsync())!;
        Assert.Null(mr.ClosedAt);
        Assert.Null(mr.MergedAt);
    }

    /// <summary>
    /// A merge is final in GitLab: it cannot be reopened. So an "opened" delivery for a merge request
    /// we already hold as Merged is a stale, out-of-order event from before the merge, not a reopen —
    /// honouring it would resurrect a deployed MR back into the plan. It must be ignored.
    /// </summary>
    [Fact]
    public async Task AStaleOpenedForAMergedMergeRequestDoesNotResurrectIt()
    {
        var repo = AddRepository();
        _db.MergeRequests.Add(new MergeRequest
        {
            Id = Guid.NewGuid(),
            ExternalId = "1",
            SourceBranch = "feature/x",
            TargetBranch = "main",
            RepositoryId = repo.Id,
            Status = MergeRequestStatus.Merged,
            MergedAt = Now.AddDays(-1),
            CreatedAt = Now.AddDays(-10)
        });
        await _db.SaveChangesAsync(Ct);

        await OpenAsync(labels: ReadyLabel);

        var mr = (await FindMrAsync())!;
        Assert.Equal(MergeRequestStatus.Merged, mr.Status);
        Assert.NotNull(mr.MergedAt);
    }

    /// <summary>
    /// The key is recorded whether or not the task exists, because nothing revisits a merge request
    /// once its own event is handled. Without it the branch would have to be re-parsed across the
    /// whole table to find this MR again, so in practice it would stay unlinked for good.
    /// </summary>
    [Fact]
    public async Task TheTaskKeyIsRecordedEvenWhenTheTaskIsUnknown()
    {
        AddRepository(trackerConnectionId: _tracker.Id);
        await _db.SaveChangesAsync(Ct);

        await OpenAsync(taskExternalId: "TASK-1");

        var mr = (await FindMrAsync())!;
        Assert.Equal("TASK-1", mr.TaskExternalId);
        Assert.Null(mr.TaskId);
    }

    [Fact]
    public async Task AKnownTaskIsLinked()
    {
        AddRepository(trackerConnectionId: _tracker.Id);
        var task = AddTask("TASK-1");
        await _db.SaveChangesAsync(Ct);

        await OpenAsync(taskExternalId: "TASK-1");

        Assert.Equal(task.Id, (await FindMrAsync())!.TaskId);
    }

    /// <summary>
    /// An unknown key is not dropped: the plan needs the task's own dependencies either way, and
    /// waiting for the task's own webhook would leave this merge request unordered until something
    /// else happened to touch it.
    /// </summary>
    [Fact]
    public async Task AnUnknownTaskIsRequestedFromTheRepositorysTracker()
    {
        AddRepository(trackerConnectionId: _tracker.Id);
        await _db.SaveChangesAsync(Ct);

        await OpenAsync(taskExternalId: "TASK-1");

        var sync = _bus.SingleSent<TaskSyncRequested>();
        Assert.Equal("TASK-1", sync.ExternalId);
        Assert.Equal("tracker", sync.TrackerConnectionName);
    }

    /// <summary>
    /// With no tracker on the repository there is nothing to ask, so asking would mean guessing
    /// which tracker owns the key.
    /// </summary>
    [Fact]
    public async Task WithNoTrackerOnTheRepositoryNoSyncIsRequested()
    {
        AddRepository(trackerConnectionId: null);
        await _db.SaveChangesAsync(Ct);

        await OpenAsync(taskExternalId: "TASK-1");

        Assert.False(_bus.AnySent<TaskSyncRequested>());
        Assert.Equal("TASK-1", (await FindMrAsync())!.TaskExternalId);
    }

    /// <summary>
    /// Issue keys are a per-tracker namespace: two trackers can both hold PROJ-1. Linking to
    /// whichever row came back first would order the deploy by a dependency from an unrelated
    /// system — a missing link costs ordering, a wrong link corrupts it.
    /// </summary>
    [Fact]
    public async Task AnAmbiguousKeyLinksNothingRatherThanGuessing()
    {
        var other = new TrackerConnection
        {
            Id = Guid.NewGuid(),
            Name = "other-tracker",
            ProviderType = "fake",
            ApiUrl = "https://other.example.com"
        };
        _db.TrackerConnections.Add(other);

        // No tracker on the repository, so the key can only be matched globally — and it is not unique.
        AddRepository(trackerConnectionId: null);
        AddTask("TASK-1", _tracker.Id);
        AddTask("TASK-1", other.Id);
        await _db.SaveChangesAsync(Ct);

        await OpenAsync(taskExternalId: "TASK-1");

        Assert.Null((await FindMrAsync())!.TaskId);
    }

    /// <summary>
    /// The repository's tracker disambiguates the same key, which is the reason the column exists.
    /// </summary>
    [Fact]
    public async Task TheRepositorysTrackerResolvesAKeyTwoTrackersShare()
    {
        var other = new TrackerConnection
        {
            Id = Guid.NewGuid(),
            Name = "other-tracker",
            ProviderType = "fake",
            ApiUrl = "https://other.example.com"
        };
        _db.TrackerConnections.Add(other);

        AddRepository(trackerConnectionId: _tracker.Id);
        var ours = AddTask("TASK-1", _tracker.Id);
        AddTask("TASK-1", other.Id);
        await _db.SaveChangesAsync(Ct);

        await OpenAsync(taskExternalId: "TASK-1");

        Assert.Equal(ours.Id, (await FindMrAsync())!.TaskId);
    }

    /// <summary>
    /// Requested unconditionally, including when the merge request already existed. Returning early
    /// on "exists" meant a redelivery after a crash stored the MR but never replanned it, and a
    /// label change never reached the plan at all — the plan silently lagging the data.
    /// </summary>
    [Fact]
    public async Task EveryEventAsksForAReplanIncludingOnesThatChangedNothing()
    {
        AddRepository();
        await _db.SaveChangesAsync(Ct);

        await OpenAsync();
        await OpenAsync();

        Assert.Equal(2, _bus.AllSent<ReleasePlanRecalculationRequested>().Count);
    }

    // ---- label persistence (E2) -----------------------------------------------

    private Task<List<MergeRequestLabelChange>> LabelRowsAsync() =>
        _db.MergeRequestLabelChanges.AsNoTracking().OrderBy(c => c.At).ToListAsync(Ct);

    /// <summary>
    /// The full label set is stored in canonical form — lower-cased, de-duplicated, sorted — which is
    /// the form the readiness gate compares against, and a change is journalled from empty.
    /// </summary>
    [Fact]
    public async Task LabelsArePersistedInCanonicalFormAndJournalled()
    {
        AddRepository();
        await _db.SaveChangesAsync(Ct);

        await OpenAsync(labels: ["Ready-For-Prod", "bug", "ready-for-prod"]);

        Assert.Equal("bug,ready-for-prod", (await FindMrAsync())!.Labels);

        var row = Assert.Single(await LabelRowsAsync());
        Assert.Equal("", row.FromLabels);
        Assert.Equal("bug,ready-for-prod", row.ToLabels);
        Assert.Equal("1", row.MergeRequestExternalId);
    }

    /// <summary>A re-sent open event reasserting the same labels is not a change and writes no row.</summary>
    [Fact]
    public async Task UnchangedLabelsWriteNoSecondJournalRow()
    {
        AddRepository();
        await _db.SaveChangesAsync(Ct);

        await OpenAsync(labels: ["qa"]);
        // Different spelling, same canonical set — must still be a no-op.
        await OpenAsync(labels: ["QA"]);

        Assert.Single(await LabelRowsAsync());
    }

    /// <summary>Removing a label is a real change and is recorded, from the old set to the new.</summary>
    [Fact]
    public async Task RemovingALabelIsRecorded()
    {
        AddRepository();
        await _db.SaveChangesAsync(Ct);

        await OpenAsync(labels: ["ready-for-prod", "qa"]);
        await OpenAsync(labels: ["qa"]);

        Assert.Equal("qa", (await FindMrAsync())!.Labels);

        var rows = await LabelRowsAsync();
        Assert.Equal(2, rows.Count);
        Assert.Equal("qa,ready-for-prod", rows[1].FromLabels);
        Assert.Equal("qa", rows[1].ToLabels);
    }

    /// <summary>A merge request that arrives with no labels records nothing: empty to empty is no change.</summary>
    [Fact]
    public async Task ANewMergeRequestWithNoLabelsWritesNoLabelRow()
    {
        AddRepository();
        await _db.SaveChangesAsync(Ct);

        await OpenAsync();

        Assert.Equal("", (await FindMrAsync())!.Labels);
        Assert.Empty(await LabelRowsAsync());
    }

    /// <summary>
    /// A stale opened event for an already-merged merge request does not rewrite its labels, exactly
    /// as it does not rewrite its status — both live behind the same guard.
    /// </summary>
    [Fact]
    public async Task AStaleOpenedEventDoesNotRewriteMergedLabels()
    {
        AddRepository();
        await _db.SaveChangesAsync(Ct);

        await OpenAsync(labels: ["ready-for-prod"]);

        // Force the stored MR terminal, as a merge webhook would have.
        var stored = await _db.MergeRequests.FirstAsync(Ct);
        stored.Status = MergeRequestStatus.Merged;
        await _db.SaveChangesAsync(Ct);

        // A late, out-of-order opened event carrying different labels must be ignored wholesale.
        await OpenAsync(labels: ["ready-for-test"]);

        Assert.Equal("ready-for-prod", (await FindMrAsync())!.Labels);
        Assert.Single(await LabelRowsAsync());
    }
}
