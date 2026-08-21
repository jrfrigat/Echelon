using Echelon.Infrastructure.ReleasePlanning;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Echelon.UnitTests.ReleasePlanning;

/// <summary>
/// Writing a plan while the provider is configured to retry transient failures.
/// </summary>
/// <remarks>
/// <para>
/// Both real providers run with <c>EnableRetryOnFailure</c>, and a retrying execution strategy
/// refuses a transaction it did not open: "the configured execution strategy
/// 'SqlServerRetryingExecutionStrategy' does not support user-initiated transactions". The planner
/// opened its own, so <c>POST /api/tasks/{id}/plan/recalculate</c> failed with a 500 on every real
/// deployment - and every test here passed, because SQLite has no retrying strategy at all.
/// </para>
/// <para>
/// That gap is what these tests close. The strategy below retries nothing; all that matters is that
/// it reports itself as retrying, which is the condition Entity Framework refuses a foreign
/// transaction under. Without the fix both tests fail with that exact message.
/// </para>
/// </remarks>
public class PlanWriteRetryTests : PlannerTestBase
{
    /// <inheritdoc/>
    protected override void ConfigureSqlite(SqliteDbContextOptionsBuilder sqlite) =>
        sqlite.ExecutionStrategy(dependencies => new RetryingStrategy(dependencies));

    private RolloutPlanner NewPlanner() =>
        new(Db, new FakeTimeProvider(Now), NullLogger<RolloutPlanner>.Instance);

    [Fact]
    public async Task RecalculateStoresThePlan()
    {
        var task = AddTask("ECH-1");
        AddMergeRequest(AddRepository("api"), task);
        await Db.SaveChangesAsync(Ct);

        var plan = await NewPlanner().RecalculateAsync(task.Id, actor: null, Ct);

        Assert.Equal(1, plan.Version);
        Assert.Single(await Db.RolloutPlans.Where(p => p.TargetTaskId == task.Id && p.IsActive).ToListAsync(Ct));
    }

    [Fact]
    public async Task RecalculateTwiceSupersedesTheFirstVersion()
    {
        var task = AddTask("ECH-2");
        AddMergeRequest(AddRepository("api"), task);
        await Db.SaveChangesAsync(Ct);

        var planner = NewPlanner();
        await planner.RecalculateAsync(task.Id, actor: null, Ct);
        var second = await planner.RecalculateAsync(task.Id, actor: null, Ct);

        // Two versions, one of them active: the deactivate-then-insert pair still commits as a unit
        // now that the transaction lives inside the strategy.
        Assert.Equal(2, second.Version);
        Assert.Equal(2, await Db.RolloutPlans.CountAsync(p => p.TargetTaskId == task.Id, Ct));
        Assert.Single(await Db.RolloutPlans.Where(p => p.TargetTaskId == task.Id && p.IsActive).ToListAsync(Ct));
    }

    [Fact]
    public async Task ImportStoresTheDeltasAndThePlanTogether()
    {
        var parent = AddTask("ECH-3");
        var child = AddTask("ECH-4");
        AddChild(parent, child);
        AddMergeRequest(AddRepository("api"), parent);
        AddMergeRequest(AddRepository("web"), child);
        await Db.SaveChangesAsync(Ct);

        var planner = NewPlanner();
        await planner.RecalculateAsync(parent.Id, actor: null, Ct);
        var document = await planner.ExportPlanYamlAsync(parent.Id, Ct);

        // Import nests: it opens the transaction and StoreAsync joins it. Only the outer one may be
        // the retriable unit - a strategy inside a strategy is refused as well.
        var imported = await planner.ImportPlanAsync(parent.Id, document!, force: false, actor: null, Ct);

        Assert.True(imported.Accepted);
        Assert.NotNull(imported.Plan);
    }

    /// <summary>An execution strategy that retries nothing but declares that it would.</summary>
    /// <remarks>
    /// <see cref="ExecutionStrategy.RetriesOnFailure"/> is what makes Entity Framework reject a
    /// transaction the strategy did not open, so declaring it is enough to reproduce what SQL Server
    /// does here. Retrying for real would need a transient failure to inject, which this does not
    /// need: the bug was that the transaction existed at all, not that a retry went wrong.
    /// </remarks>
    private sealed class RetryingStrategy(ExecutionStrategyDependencies dependencies)
        : ExecutionStrategy(dependencies, maxRetryCount: 3, maxRetryDelay: TimeSpan.Zero)
    {
        protected override bool ShouldRetryOn(Exception exception) => false;
    }
}
