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
/// Where a merge request leaves the plan. Everything here is about a merged merge request
/// staying merged: the status it lands on decides whether the planner still deploys it and
/// whether archiving will ever be allowed to remove it.
/// </summary>
/// <remarks>
/// The handler is called directly with a real SQLite database and a <see cref="RecordingBus"/> that
/// captures the replan it forwards. An unknown merge request throws — which is what makes Rebus
/// fault and redeliver — so those cases assert the throw rather than inspecting a harness.
/// </remarks>
public sealed class MrStatusChangedConsumerTests : IAsyncLifetime
{
    private static readonly DateTime Now = new(2026, 7, 17, 12, 0, 0, DateTimeKind.Utc);
    private static CancellationToken Ct => CancellationToken.None;

    private SqliteConnection _connection = null!;
    private AppDbContext _db = null!;
    private RecordingBus _bus = null!;
    private MrStatusChangedConsumer _handler = null!;

    private VcsConnection _vcs = null!;
    private Repository _repo = null!;

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        await _connection.OpenAsync(Ct);
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);
        await _db.Database.EnsureCreatedAsync(Ct);

        _bus = new RecordingBus();
        _handler = new MrStatusChangedConsumer(_db, _bus, new FakeTimeProvider(Now), NullLogger<MrStatusChangedConsumer>.Instance);

        _vcs = new VcsConnection
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
            ConnectionId = _vcs.Id
        };
        _db.VcsConnections.Add(_vcs);
        _db.Repositories.Add(_repo);
        await _db.SaveChangesAsync(Ct);
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        await _connection.DisposeAsync();
    }

    private MergeRequest AddMergeRequest(MergeRequestStatus status, bool isStatusManual = false)
    {
        var mr = new MergeRequest
        {
            Id = Guid.NewGuid(),
            ExternalId = "1",
            SourceBranch = "feature/x",
            TargetBranch = "main",
            RepositoryId = _repo.Id,
            Status = status,
            IsStatusManual = isStatusManual,
            CreatedAt = Now.AddDays(-1)
        };
        _db.MergeRequests.Add(mr);
        return mr;
    }

    private Task ChangeAsync(
        MergeRequestStatus newStatus,
        string repositoryExternalId = "group/api",
        string mrId = "1",
        DateTime? changedAt = null,
        IReadOnlyList<string>? labels = null) =>
        _handler.Handle(new MrStatusChanged(
            "gitlab", repositoryExternalId, mrId, newStatus, changedAt ?? Now, labels ?? []));

    private Task<MergeRequest> ReloadAsync() =>
        _db.MergeRequests.AsNoTracking().FirstAsync(m => m.ExternalId == "1", Ct);

    [Fact]
    public async Task MergingRecordsTheMergeTimeAndTheStatus()
    {
        AddMergeRequest(MergeRequestStatus.ReadyForDeploy);
        await _db.SaveChangesAsync(Ct);

        await ChangeAsync(MergeRequestStatus.Merged, changedAt: Now.AddMinutes(-5));

        var mr = await ReloadAsync();
        Assert.Equal(MergeRequestStatus.Merged, mr.Status);
        Assert.Equal(Now.AddMinutes(-5), mr.MergedAt);
        Assert.Null(mr.ClosedAt);
    }

    // ---- labels at merge (E2) -------------------------------------------------

    /// <summary>
    /// A merge event carries the final label set, and captures it: a merged merge request has left
    /// the open-request listing, so this is the last chance to learn what it was ready for.
    /// </summary>
    [Fact]
    public async Task MergingCapturesTheFinalLabels()
    {
        AddMergeRequest(MergeRequestStatus.ReadyForDeploy);
        await _db.SaveChangesAsync(Ct);

        await ChangeAsync(MergeRequestStatus.Merged, labels: ["Ready-For-Prod", "qa"]);

        Assert.Equal("qa,ready-for-prod", (await ReloadAsync()).Labels);
        Assert.Single(await _db.MergeRequestLabelChanges.AsNoTracking().ToListAsync(Ct));
    }

    /// <summary>
    /// An event that carries no labels leaves the stored set alone. Empty means "no label
    /// information here" — an older message from before the field existed — not "labels removed";
    /// wiping the set captured from the opened events would be the wrong reading.
    /// </summary>
    [Fact]
    public async Task AStatusChangeWithoutLabelsLeavesTheStoredSetAlone()
    {
        var mr = AddMergeRequest(MergeRequestStatus.ReadyForDeploy);
        mr.Labels = "ready-for-prod";
        await _db.SaveChangesAsync(Ct);

        await ChangeAsync(MergeRequestStatus.Merged, labels: []);

        Assert.Equal("ready-for-prod", (await ReloadAsync()).Labels);
        Assert.Empty(await _db.MergeRequestLabelChanges.AsNoTracking().ToListAsync(Ct));
    }

    /// <summary>
    /// A closed merge request never gets a merge time, so archiving keys off this column instead —
    /// leave it unset and the row is ineligible for archiving forever.
    /// </summary>
    [Fact]
    public async Task ClosingRecordsTheCloseTimeInItsOwnColumn()
    {
        AddMergeRequest(MergeRequestStatus.Opened);
        await _db.SaveChangesAsync(Ct);

        await ChangeAsync(MergeRequestStatus.Closed, changedAt: Now.AddMinutes(-5));

        var mr = await ReloadAsync();
        Assert.Equal(Now.AddMinutes(-5), mr.ClosedAt);
        Assert.Null(mr.MergedAt);
    }

    /// <summary>
    /// The claim this consumer exists to keep. Deliveries are concurrent and unordered, so an
    /// "opened" raised before a merge can arrive after it — and applying it would put a merged
    /// merge request back in the deploy plan.
    /// </summary>
    [Theory]
    [InlineData(MergeRequestStatus.Opened)]
    [InlineData(MergeRequestStatus.Reviewed)]
    [InlineData(MergeRequestStatus.ReadyForDeploy)]
    public async Task ANonTerminalStatusCannotResurrectAMergedMergeRequest(MergeRequestStatus late)
    {
        AddMergeRequest(MergeRequestStatus.Merged);
        await _db.SaveChangesAsync(Ct);

        await ChangeAsync(late);

        Assert.Equal(MergeRequestStatus.Merged, (await ReloadAsync()).Status);
    }

    /// <summary>
    /// Terminal to terminal still applies: a merge request can be closed after merging in the
    /// VCS's own telling, and refusing every change once terminal would freeze the first one.
    /// </summary>
    [Fact]
    public async Task OneTerminalStatusMayFollowAnother()
    {
        AddMergeRequest(MergeRequestStatus.Merged);
        await _db.SaveChangesAsync(Ct);

        await ChangeAsync(MergeRequestStatus.Closed, changedAt: Now.AddMinutes(-1));

        Assert.Equal(MergeRequestStatus.Closed, (await ReloadAsync()).Status);
    }

    /// <summary>
    /// An operator's pin holds against labels, not against the VCS reporting the merge request
    /// actually merged. Keeping the pin would leave a merged MR pinned into the plan by hand.
    /// </summary>
    [Fact]
    public async Task ATerminalStatusClearsAManualPin()
    {
        AddMergeRequest(MergeRequestStatus.ReadyForDeploy, isStatusManual: true);
        await _db.SaveChangesAsync(Ct);

        await ChangeAsync(MergeRequestStatus.Merged);

        var mr = await ReloadAsync();
        Assert.Equal(MergeRequestStatus.Merged, mr.Status);
        Assert.False(mr.IsStatusManual);
    }

    /// <summary>
    /// The opened event is probably still in flight, and this is what retry is for. Throwing is what
    /// makes Rebus redeliver; swallowing it discarded the merge event for good, leaving a merged
    /// merge request in the plan forever.
    /// </summary>
    [Fact]
    public async Task AnUnknownMergeRequestThrowsSoTheEventIsRetriedRatherThanLost()
    {
        await Assert.ThrowsAsync<MergeRequestNotYetKnownException>(() => ChangeAsync(MergeRequestStatus.Merged));

        Assert.False(_bus.AnySent<ReleasePlanRecalculationRequested>());
    }

    /// <summary>
    /// An absent repository is configuration, not a race: redelivering will not conjure one, so
    /// this is the one "not found" here that must not throw.
    /// </summary>
    [Fact]
    public async Task AnUnknownRepositoryIsIgnoredRatherThanRetriedForever()
    {
        var exception = await Record.ExceptionAsync(
            () => ChangeAsync(MergeRequestStatus.Merged, repositoryExternalId: "group/never-configured"));

        Assert.Null(exception);
    }

    [Fact]
    public async Task AnAppliedChangeAsksForAReplan()
    {
        AddMergeRequest(MergeRequestStatus.ReadyForDeploy);
        await _db.SaveChangesAsync(Ct);

        await ChangeAsync(MergeRequestStatus.Merged);

        Assert.True(_bus.AnySent<ReleasePlanRecalculationRequested>());
    }

    /// <summary>
    /// A change that was refused changed nothing, so there is nothing to replan — and asking anyway
    /// would rebuild the plan on every out-of-order delivery.
    /// </summary>
    [Fact]
    public async Task ARefusedChangeAsksForNothing()
    {
        AddMergeRequest(MergeRequestStatus.Merged);
        await _db.SaveChangesAsync(Ct);

        await ChangeAsync(MergeRequestStatus.Opened);

        Assert.False(_bus.AnySent<ReleasePlanRecalculationRequested>());
    }
}
