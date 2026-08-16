using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using ReleaseOrchestrator.Core.Enums;
using ReleaseOrchestrator.Infrastructure.Persistence;
using ReleaseOrchestrator.Infrastructure.Persistence.Models;
using ReleaseOrchestrator.Infrastructure.Vcs;
using ReleaseOrchestrator.Providers.Abstractions;
using ReleaseOrchestrator.Providers.Abstractions.Vcs;
using Xunit;

namespace ReleaseOrchestrator.UnitTests.Vcs;

/// <summary>
/// The reconcile path exists to catch what webhooks missed, so it must refresh the full label set
/// the per-environment readiness gate reads - not only the coarse ready-for-deploy status.
/// </summary>
/// <remarks>
/// The gate compares <see cref="MergeRequest.Labels"/>. The reconcile re-derived the status from the
/// provider's labels but never wrote that set back, so a missed "label removed" delivery left the
/// gate admitting to an environment on a label the provider no longer reports - the one direction a
/// gate must never fail. These run against SQLite, the same offline database the other persistence
/// tests use.
/// </remarks>
public sealed class VcsServiceTests : IAsyncLifetime
{
    private static readonly DateTime Now = new(2026, 7, 25, 12, 0, 0, DateTimeKind.Utc);
    private static CancellationToken Ct => CancellationToken.None;

    private SqliteConnection _conn = null!;
    private AppDbContext _db = null!;

    public async Task InitializeAsync()
    {
        _conn = new SqliteConnection("DataSource=:memory:");
        await _conn.OpenAsync(Ct);
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_conn).Options);
        await _db.Database.EnsureCreatedAsync(Ct);
    }

    public async Task DisposeAsync()
    {
        await _db.DisposeAsync();
        await _conn.DisposeAsync();
    }

    private async Task<(Guid RepositoryId, string MrExternalId)> SeedAsync(string storedLabels)
    {
        var connection = new VcsConnection
        {
            Id = Guid.NewGuid(),
            Name = "conn",
            ProviderType = "gitlab",
            ApiUrl = "https://gitlab.example.com"
        };
        var repository = new Repository
        {
            Id = Guid.NewGuid(),
            Name = "repo",
            ExternalId = "group/repo",
            ConnectionId = connection.Id
        };
        var mr = new MergeRequest
        {
            Id = Guid.NewGuid(),
            ExternalId = "1",
            SourceBranch = "feature/PROJ-1",
            TargetBranch = "main",
            RepositoryId = repository.Id,
            Status = MergeRequestStatus.ReadyForDeploy,
            Labels = storedLabels,
            CreatedAt = Now
        };

        _db.VcsConnections.Add(connection);
        _db.Repositories.Add(repository);
        _db.MergeRequests.Add(mr);
        await _db.SaveChangesAsync(Ct);
        return (repository.Id, mr.ExternalId);
    }

    private VcsService Service(VcsMergeRequest info) =>
        new(_db, new FakeVcsProviderFactory(new FakeVcsProvider(info)),
            new FakeTimeProvider(Now), NullLogger<VcsService>.Instance);

    [Fact]
    public async Task Reconcile_PersistsTheCurrentLabelSet_ForTheReadinessGate()
    {
        var (repoId, mrId) = await SeedAsync(storedLabels: "");
        var info = new VcsMergeRequest(
            "1", "feature/PROJ-1", "main", MergeRequestStatus.Opened, "t", Now, null,
            ["ready-for-prod", "ready-for-test"]);

        await Service(info).SyncMergeRequestAsync(repoId, mrId, Ct);

        var mr = await _db.MergeRequests.SingleAsync(Ct);
        Assert.Equal("ready-for-prod,ready-for-test", mr.Labels);
        // The change is journalled, so the readiness history can answer "when did it become ready".
        Assert.True(await _db.MergeRequestLabelChanges.AnyAsync(Ct));
    }

    /// <summary>
    /// The dangerous case: a "ready-for-prod removed" delivery was missed, so the stored set still
    /// carries it. The reconcile must drop it - otherwise the gate keeps admitting to production.
    /// </summary>
    [Fact]
    public async Task Reconcile_ClearsALabelTheProviderNoLongerReports()
    {
        var (repoId, mrId) = await SeedAsync(storedLabels: "ready-for-prod");
        var info = new VcsMergeRequest(
            "1", "feature/PROJ-1", "main", MergeRequestStatus.Opened, "t", Now, null,
            []);

        await Service(info).SyncMergeRequestAsync(repoId, mrId, Ct);

        var mr = await _db.MergeRequests.SingleAsync(Ct);
        Assert.Equal(string.Empty, mr.Labels);
    }

    /// <summary>
    /// When the provider cannot report labels, an empty list means "cannot say", not "none": the
    /// reconcile must leave the set the webhook captured alone rather than wipe it on missing evidence.
    /// </summary>
    [Fact]
    public async Task Reconcile_LeavesLabelsAlone_WhenTheProviderCannotReportThem()
    {
        var (repoId, mrId) = await SeedAsync(storedLabels: "ready-for-prod");
        var info = new VcsMergeRequest(
            "1", "feature/PROJ-1", "main", MergeRequestStatus.Opened, "t", Now, null,
            []);

        var service = new VcsService(
            _db, new FakeVcsProviderFactory(new FakeVcsProvider(info, supportsLabels: false)),
            new FakeTimeProvider(Now), NullLogger<VcsService>.Instance);
        await service.SyncMergeRequestAsync(repoId, mrId, Ct);

        var mr = await _db.MergeRequests.SingleAsync(Ct);
        Assert.Equal("ready-for-prod", mr.Labels);
    }

    /// <summary>The reconcile reads the pipeline result from the API and stores it for the readiness gate.</summary>
    [Fact]
    public async Task Reconcile_PersistsThePipelineResult()
    {
        var (repoId, mrId) = await SeedAsync(storedLabels: "");
        var info = new VcsMergeRequest(
            "1", "feature/PROJ-1", "main", MergeRequestStatus.Opened, "t", Now, null,
            [], PipelineStatus: "success");

        await Service(info).SyncMergeRequestAsync(repoId, mrId, Ct);

        var mr = await _db.MergeRequests.SingleAsync(Ct);
        Assert.Equal("success", mr.PipelineResult);
    }

    private sealed class FakeVcsProviderFactory(IVcsProvider provider) : IVcsProviderFactory
    {
        public IReadOnlyCollection<string> AvailableProviders => ["gitlab"];

        public IReadOnlyList<ProviderSettingSchema> GetSettingsSchema(string providerType) => [];

        public Task<IVcsProvider> CreateAsync(VcsConnectionDescriptor connection, CancellationToken ct) =>
            Task.FromResult(provider);
    }

    private sealed class FakeVcsProvider(VcsMergeRequest info, bool supportsLabels = true) : IVcsProvider
    {
        public VcsCapabilities Capabilities { get; } = new() { SupportsMergeRequestLabels = supportsLabels };

        public Task<VcsMergeRequest?> GetMergeRequestAsync(string projectPath, string mergeRequestId, CancellationToken ct) =>
            Task.FromResult<VcsMergeRequest?>(info);

        public Task<IReadOnlyList<VcsMergeRequest>> GetOpenMergeRequestsAsync(string projectPath, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<VcsMergeRequest>>([]);

        public string? ParseTaskKeyFromBranch(string? branchName) => null;
    }
}
