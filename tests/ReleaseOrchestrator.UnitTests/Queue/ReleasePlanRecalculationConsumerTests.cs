using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using ReleaseOrchestrator.Application.Contracts.Messages;
using ReleaseOrchestrator.Application.DTOs;
using ReleaseOrchestrator.Application.Services;
using ReleaseOrchestrator.Core.Enums;
using ReleaseOrchestrator.Infrastructure.Persistence;
using ReleaseOrchestrator.Infrastructure.Persistence.Models;
using ReleaseOrchestrator.Infrastructure.Queue.Consumers;
using ReleaseOrchestrator.Infrastructure.ReleasePlanning;
using Xunit;

namespace ReleaseOrchestrator.UnitTests.Queue;

/// <summary>
/// The handler that replaced the debounce timer.
/// </summary>
/// <remarks>
/// The old design handed the request to an in-process timer and acknowledged the message at once:
/// a restart inside the 15-second window lost the recalculation outright, and every replica ran
/// its own timer over the same data. The claim that replaced it — "a burst of N events becomes N
/// messages: the first rebuilds, the rest find a plan whose snapshot already covers them" — is the
/// one worth pinning, because if coalescing fails the service still works and merely rebuilds the
/// plan once per webhook, which nothing would notice until the load did.
///
/// The handler is a plain class, so a test builds it and calls <c>Handle</c> directly — no broker,
/// no harness. The planner behind it is the real one over SQLite, not a stub: coalescing is a claim
/// about what SnapshotStartedAt means, and a stub would only assert that the test knows its own
/// answer.
/// </remarks>
public sealed class ReleasePlanRecalculationConsumerTests : IAsyncLifetime
{
    private static readonly DateTime Now = new(2026, 7, 17, 12, 0, 0, DateTimeKind.Utc);
    private static CancellationToken Ct => CancellationToken.None;

    private SqliteConnection _connection = null!;
    private AppDbContext _db = null!;
    private CountingPlanner _planner = null!;
    private ReleasePlanRecalculationConsumer _handler = null!;

    public async Task InitializeAsync()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        await _connection.OpenAsync(Ct);
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options);
        await _db.Database.EnsureCreatedAsync(Ct);

        _planner = new CountingPlanner(
            new ReleasePlanner(_db, new FakeTimeProvider(Now), NullLogger<ReleasePlanner>.Instance));
        _handler = new ReleasePlanRecalculationConsumer(_planner, NullLogger<ReleasePlanRecalculationConsumer>.Instance);
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        await _connection.DisposeAsync();
    }

    /// <summary>
    /// The real planner, counting rebuilds — the number the coalescing claim is about.
    /// </summary>
    private sealed class CountingPlanner(IReleasePlannerService inner) : IReleasePlannerService
    {
        public int Recalculations { get; private set; }
        public Exception? Throws { get; set; }

        public Task<bool> IsPlanCurrentAsync(DateTime requestedAt, CancellationToken ct = default) =>
            inner.IsPlanCurrentAsync(requestedAt, ct);

        public Task<ReleasePlanDto> RecalculateAsync(CancellationToken ct = default)
        {
            Recalculations++;
            return Throws is not null ? Task.FromException<ReleasePlanDto>(Throws) : inner.RecalculateAsync(ct);
        }

        public Task<ReleasePlanDto?> GetActiveAsync(CancellationToken ct = default) => inner.GetActiveAsync(ct);
        public Task<ReleasePlanDto?> GetByIdAsync(Guid planId, CancellationToken ct = default) => inner.GetByIdAsync(planId, ct);
        public Task<string> ExportToYamlAsync(Guid planId, CancellationToken ct = default) => inner.ExportToYamlAsync(planId, ct);
        public Task<ReleasePlanDto> ImportFromYamlAsync(string yaml, bool force = false, CancellationToken ct = default) => inner.ImportFromYamlAsync(yaml, force, ct);
        public Task<ReleasePlanDto> ReorderStagesAsync(Guid planId, List<Guid> orderedStageIds, CancellationToken ct = default) => inner.ReorderStagesAsync(planId, orderedStageIds, ct);
        public Task<ReleasePlanDto> MoveItemAsync(Guid planId, Guid itemId, Guid targetStageId, CancellationToken ct = default) => inner.MoveItemAsync(planId, itemId, targetStageId, ct);
        public Task<ReleasePlanDto> AddItemAsync(Guid planId, Guid stageId, Guid mrId, CancellationToken ct = default) => inner.AddItemAsync(planId, stageId, mrId, ct);
        public Task<ReleasePlanDto> RemoveItemAsync(Guid planId, Guid itemId, CancellationToken ct = default) => inner.RemoveItemAsync(planId, itemId, ct);
    }

    private async Task AddReadyMergeRequestAsync()
    {
        var vcs = new VcsConnection
        {
            Id = Guid.NewGuid(),
            Name = $"vcs-{Guid.NewGuid():N}",
            ProviderType = "gitlab",
            ApiUrl = "https://gitlab.example.com"
        };
        var repo = new Repository
        {
            Id = Guid.NewGuid(),
            Name = "api",
            ExternalId = $"group/api-{Guid.NewGuid():N}",
            ConnectionId = vcs.Id
        };
        _db.VcsConnections.Add(vcs);
        _db.Repositories.Add(repo);
        _db.MergeRequests.Add(new MergeRequest
        {
            Id = Guid.NewGuid(),
            ExternalId = "1",
            SourceBranch = "feature/x",
            TargetBranch = "main",
            RepositoryId = repo.Id,
            Status = MergeRequestStatus.ReadyForDeploy,
            CreatedAt = Now.AddDays(-1)
        });
        await _db.SaveChangesAsync(Ct);
    }

    private Task RequestAsync(DateTime requestedAt) =>
        _handler.Handle(new ReleasePlanRecalculationRequested(requestedAt, "test"));

    [Fact]
    public async Task ARequestWithNoPlanYetRebuilds()
    {
        await AddReadyMergeRequestAsync();

        await RequestAsync(Now.AddSeconds(-1));

        Assert.Equal(1, _planner.Recalculations);
        Assert.Single(await _db.ReleasePlans.ToListAsync(Ct));
    }

    /// <summary>
    /// The claim itself. A merge request opening triggers several events at once, and each becomes
    /// its own message; rebuilding per message is the load the timer existed to prevent.
    /// </summary>
    [Fact]
    public async Task ABurstOfRequestsProducesOnePlan()
    {
        await AddReadyMergeRequestAsync();
        var requestedAt = Now.AddSeconds(-1);

        for (int i = 0; i < 5; i++)
            await RequestAsync(requestedAt);

        Assert.Equal(1, _planner.Recalculations);
        Assert.Single(await _db.ReleasePlans.ToListAsync(Ct));
    }

    /// <summary>
    /// The other half: a request raised after the plan's snapshot began describes a change the plan
    /// cannot contain, so it must not be coalesced away. Skipping it strands the change until the
    /// next unrelated event — the reason the comparison is against SnapshotStartedAt and not
    /// CreatedAt.
    /// </summary>
    [Fact]
    public async Task AChangeThatArrivedAfterTheSnapshotStillRebuilds()
    {
        await AddReadyMergeRequestAsync();

        await RequestAsync(Now.AddSeconds(-1));
        Assert.Equal(1, _planner.Recalculations);

        await RequestAsync(Now.AddSeconds(1));

        Assert.Equal(2, _planner.Recalculations);
        Assert.Equal(2, await _db.ReleasePlans.CountAsync(Ct));
    }

    /// <summary>
    /// A manual plan is active, so recalculation stores its result inactive — and that plan is not
    /// AutoGenerated, so it must not make the next request look covered. Coalescing against it
    /// would skip every rebuild for as long as the import stays active.
    /// </summary>
    [Fact]
    public async Task AnImportedPlanDoesNotMakeARequestLookCovered()
    {
        await AddReadyMergeRequestAsync();
        _db.ReleasePlans.Add(new ReleasePlan
        {
            Id = Guid.NewGuid(),
            Name = "imported",
            Version = "1",
            IsActive = true,
            AutoGenerated = false,
            CreatedAt = Now,
            UpdatedAt = Now,
            SnapshotStartedAt = Now.AddHours(1)
        });
        await _db.SaveChangesAsync(Ct);

        await RequestAsync(Now.AddSeconds(-1));

        Assert.Equal(1, _planner.Recalculations);
    }

    /// <summary>
    /// The work runs inside the handler so the broker acknowledges only once the plan exists. If the
    /// handler swallowed the failure the recalculation would be dropped silently — which is exactly
    /// what the debounce timer did on restart. Letting it propagate is what makes Rebus fault the
    /// message and redeliver it.
    /// </summary>
    [Fact]
    public async Task AFailedRecalculationPropagatesSoTheMessageIsRedelivered()
    {
        _planner.Throws = new InvalidOperationException("database is down");

        await Assert.ThrowsAsync<InvalidOperationException>(() => RequestAsync(Now.AddSeconds(-1)));

        Assert.Empty(await _db.ReleasePlans.ToListAsync(Ct));
    }
}
