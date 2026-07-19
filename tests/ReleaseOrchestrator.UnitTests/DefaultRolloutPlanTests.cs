using ReleaseOrchestrator.Application.ReleasePlanning;
using ReleaseOrchestrator.Core.Enums;
using Xunit;

namespace ReleaseOrchestrator.UnitTests;

/// <summary>
/// Covers the default rollout plan: what the repository-ordering rules add up to, shown to an
/// operator as an order rather than as a list of pairs.
/// </summary>
/// <remarks>
/// The derivation delegates to <see cref="ReleasePlanGraph"/> on purpose, so these are less about
/// the sort itself -- that is covered in <see cref="ReleasePlanGraphTests"/> -- than about the
/// translation being faithful: that the waves come back keyed by repository, that an unconfigured
/// repository still appears, and that a contradiction is reported rather than quietly resolved.
/// </remarks>
public class DefaultRolloutPlanTests
{
    private static DefaultPlanRepository Repo(params PlanRepositoryLink[] dependsOn) =>
        new(Guid.NewGuid(), dependsOn);

    private static PlanRepositoryLink After(DefaultPlanRepository repo, StackDependencyType type = StackDependencyType.Hard) =>
        new(repo.Id, type);

    private static int WaveOf(DefaultPlanResult plan, DefaultPlanRepository repo) =>
        plan.Waves.FindIndex(wave => wave.Contains(repo.Id));

    /// <summary>
    /// An unconfigured repository is unconstrained, not excluded. Dropping it would read as "this
    /// repository is not deployed", which is the opposite of what no rules means.
    /// </summary>
    [Fact]
    public void RepositoriesWithNoRulesAllShareTheFirstWave()
    {
        var plan = DefaultRolloutPlan.Build([Repo(), Repo(), Repo()]);

        Assert.Single(plan.Waves);
        Assert.Equal(3, plan.Waves[0].Count);
        Assert.Empty(plan.Conflicts);
    }

    [Fact]
    public void ARepositoryDeploysAfterTheOneItsRuleNames()
    {
        var db = Repo();
        var api = Repo(After(db));

        // api first in the input: input order breaks ties, so a missing rule would leave them in
        // one wave and a reversed one would put api first. Either way this fails.
        var plan = DefaultRolloutPlan.Build([api, db]);

        Assert.True(WaveOf(plan, db) < WaveOf(plan, api));
    }

    [Fact]
    public void OrderingIsTransitiveAcrossWaves()
    {
        var db = Repo();
        var api = Repo(After(db));
        var web = Repo(After(api));

        var plan = DefaultRolloutPlan.Build([web, api, db]);

        Assert.Equal(3, plan.Waves.Count);
        Assert.True(WaveOf(plan, db) < WaveOf(plan, api));
        Assert.True(WaveOf(plan, api) < WaveOf(plan, web));
    }

    /// <summary>
    /// Rules that contradict each other still produce an order -- an operator needs something to
    /// look at -- but the contradiction is reported. Silently resolving it would leave a
    /// misconfiguration invisible and permanent.
    /// </summary>
    [Fact]
    public void ACycleInTheRulesIsReportedAndStillYieldsAnOrder()
    {
        var first = new DefaultPlanRepository(Guid.NewGuid(), []);
        var second = new DefaultPlanRepository(Guid.NewGuid(), [new PlanRepositoryLink(first.Id, StackDependencyType.Hard)]);
        var cyclic = first with { DependsOn = new[] { new PlanRepositoryLink(second.Id, StackDependencyType.Hard) } };

        var plan = DefaultRolloutPlan.Build([cyclic, second]);

        Assert.NotEmpty(plan.Conflicts);
        Assert.Equal(2, plan.Waves.Sum(w => w.Count));   // every repository is still placed
    }

    /// <summary>A soft rule is the one that yields, so the hard ordering survives the cycle intact.</summary>
    [Fact]
    public void BreakingACycleDropsTheSoftRuleFirst()
    {
        var first = new DefaultPlanRepository(Guid.NewGuid(), []);
        var second = new DefaultPlanRepository(Guid.NewGuid(), [new PlanRepositoryLink(first.Id, StackDependencyType.Hard)]);
        var cyclic = first with { DependsOn = new[] { new PlanRepositoryLink(second.Id, StackDependencyType.Soft) } };

        var plan = DefaultRolloutPlan.Build([cyclic, second]);

        var dropped = Assert.Single(plan.Conflicts);
        Assert.Equal(PlanEdgeKind.RepoSoft, dropped.DroppedEdgeKind);

        // The hard rule held: second still deploys after first.
        Assert.True(plan.Waves.FindIndex(w => w.Contains(first.Id))
                    < plan.Waves.FindIndex(w => w.Contains(second.Id)));
    }

    [Fact]
    public void RepositoriesThatDoNotConstrainEachOtherShareAWave()
    {
        var db = Repo();
        var api = Repo(After(db));
        var docs = Repo();

        var plan = DefaultRolloutPlan.Build([db, api, docs]);

        // docs has no rules, so it goes as early as possible -- alongside db.
        Assert.Equal(WaveOf(plan, db), WaveOf(plan, docs));
        Assert.True(WaveOf(plan, docs) < WaveOf(plan, api));
    }
}
