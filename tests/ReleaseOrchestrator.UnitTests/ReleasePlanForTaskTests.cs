using ReleaseOrchestrator.Application.ReleasePlanning;
using ReleaseOrchestrator.Core.Enums;
using Xunit;

namespace ReleaseOrchestrator.UnitTests;

/// <summary>
/// Covers the per-task projection: <see cref="PlanClosureBuilder.Closure"/> cuts a target's
/// transitive prerequisites out of the task-dependency graph, and
/// <see cref="ReleasePlanGraph.BuildForTask"/> orders just those tasks' merge requests through the
/// same engine the global plan uses. These are the P0 pieces of the plan-per-task pivot
/// (docs/issues/006-per-task-planning.md).
/// </summary>
public class ReleasePlanForTaskTests
{
    // ---- builders -------------------------------------------------------------

    private static Guid NewTask() => Guid.NewGuid();

    /// <summary>Builds a task-dependency adjacency from "dependent depends on prerequisites" pairs.</summary>
    private static Dictionary<Guid, IReadOnlyList<Guid>> DependsOn(params (Guid Dependent, Guid[] Prerequisites)[] edges) =>
        edges.ToDictionary(e => e.Dependent, e => (IReadOnlyList<Guid>)e.Prerequisites);

    private static PlanMergeRequest Mr(
        Guid taskId,
        IReadOnlyDictionary<Guid, IReadOnlyList<Guid>> dependsOn,
        Guid? repositoryId = null,
        IReadOnlyList<PlanRepositoryLink>? repoDependsOn = null) =>
        new(Guid.NewGuid(),
            taskId,
            dependsOn.TryGetValue(taskId, out var deps) ? deps : [],
            [],   // hierarchy children: exercised in ReleasePlanGraphTests, not needed for the closure cases
            repositoryId ?? Guid.NewGuid(),
            repoDependsOn ?? []);

    private static int StageOf(PlanGraphResult result, PlanMergeRequest mr) =>
        result.Stages.FindIndex(stage => stage.Contains(mr.Id));

    // ---- closure --------------------------------------------------------------

    [Fact]
    public void Closure_IsJustTheTarget_WhenItHasNoDependencies()
    {
        var target = NewTask();

        var closure = PlanClosureBuilder.Closure(target, new Dictionary<Guid, IReadOnlyList<Guid>>());

        Assert.Equal([target], closure);
    }

    [Fact]
    public void Closure_FollowsTheChain_TransitiveClosure()
    {
        Guid a = NewTask(), b = NewTask(), c = NewTask();
        // c -> b -> a  (c depends on b, b depends on a)
        var graph = DependsOn((c, [b]), (b, [a]));

        var closure = PlanClosureBuilder.Closure(c, graph);

        Assert.Equal(new[] { a, b, c }.OrderBy(x => x), closure);
    }

    [Fact]
    public void Closure_CollectsEachTaskOnce_OverADiamond()
    {
        Guid a = NewTask(), b = NewTask(), c = NewTask(), d = NewTask();
        // d depends on b and c; both depend on a.
        var graph = DependsOn((d, [b, c]), (b, [a]), (c, [a]));

        var closure = PlanClosureBuilder.Closure(d, graph);

        Assert.Equal(4, closure.Count);
        Assert.Equal(new[] { a, b, c, d }.OrderBy(x => x), closure);
    }

    [Fact]
    public void Closure_Terminates_OnACycle()
    {
        Guid a = NewTask(), b = NewTask();
        // a depends on b, b depends on a: a cycle. The closure must be finite, not loop forever.
        var graph = DependsOn((a, [b]), (b, [a]));

        var closure = PlanClosureBuilder.Closure(a, graph);

        Assert.Equal(new[] { a, b }.OrderBy(x => x), closure);
    }

    [Fact]
    public void Closure_ExcludesTasksTheTargetDoesNotDependOn()
    {
        Guid target = NewTask(), prerequisite = NewTask(), unrelated = NewTask();
        var graph = DependsOn((target, [prerequisite]), (unrelated, [prerequisite]));

        var closure = PlanClosureBuilder.Closure(target, graph);

        Assert.DoesNotContain(unrelated, closure);
        Assert.Equal(new[] { target, prerequisite }.OrderBy(x => x), closure);
    }

    // ---- BuildForTask ---------------------------------------------------------

    [Fact]
    public void BuildForTask_IncludesOnlyMergeRequestsInTheClosure()
    {
        Guid target = NewTask(), prerequisite = NewTask(), unrelated = NewTask();
        var graph = DependsOn((target, [prerequisite]));

        var targetMr = Mr(target, graph);
        var prerequisiteMr = Mr(prerequisite, graph);
        var unrelatedMr = Mr(unrelated, graph);

        var result = ReleasePlanGraph.BuildForTask(target, graph, [targetMr, prerequisiteMr, unrelatedMr]);

        Assert.NotEqual(-1, StageOf(result, targetMr));
        Assert.NotEqual(-1, StageOf(result, prerequisiteMr));
        // The unrelated task is outside the target's closure, so its merge request is not planned.
        Assert.Equal(-1, StageOf(result, unrelatedMr));
    }

    [Fact]
    public void BuildForTask_DeploysPrerequisiteBeforeTarget()
    {
        Guid target = NewTask(), prerequisite = NewTask();
        var graph = DependsOn((target, [prerequisite]));

        var targetMr = Mr(target, graph);
        var prerequisiteMr = Mr(prerequisite, graph);

        var result = ReleasePlanGraph.BuildForTask(target, graph, [targetMr, prerequisiteMr]);

        Assert.True(StageOf(result, prerequisiteMr) < StageOf(result, targetMr));
    }

    [Fact]
    public void BuildForTask_ToleratesAPrerequisiteWithNoMergeRequests()
    {
        Guid target = NewTask(), prerequisite = NewTask();
        // The prerequisite task is in the closure but ships no merge request of its own.
        var graph = DependsOn((target, [prerequisite]));

        var targetMr = Mr(target, graph);

        var result = ReleasePlanGraph.BuildForTask(target, graph, [targetMr]);

        Assert.NotEqual(-1, StageOf(result, targetMr));
        Assert.Single(result.Stages);
    }

    // ---- overrides (manual edges) --------------------------------------------

    [Fact]
    public void Build_AddEdge_OrdersOtherwiseIndependentMergeRequests()
    {
        var empty = new Dictionary<Guid, IReadOnlyList<Guid>>();
        var a = Mr(NewTask(), empty);
        var b = Mr(NewTask(), empty);

        var plain = ReleasePlanGraph.Build([a, b]);
        Assert.Equal(StageOf(plain, a), StageOf(plain, b)); // independent -> same stage

        var overridden = ReleasePlanGraph.Build([a, b], addEdges: [(a.Id, b.Id)]);
        Assert.True(StageOf(overridden, a) < StageOf(overridden, b));
    }

    [Fact]
    public void Build_RemoveEdge_DropsADerivedOrdering()
    {
        Guid prereq = NewTask(), target = NewTask();
        var graph = DependsOn((target, [prereq]));
        var prereqMr = Mr(prereq, graph);
        var targetMr = Mr(target, graph);

        var plain = ReleasePlanGraph.Build([prereqMr, targetMr]);
        Assert.True(StageOf(plain, prereqMr) < StageOf(plain, targetMr));

        var overridden = ReleasePlanGraph.Build([prereqMr, targetMr], removeEdges: [(prereqMr.Id, targetMr.Id)]);
        Assert.Equal(StageOf(overridden, prereqMr), StageOf(overridden, targetMr)); // dropped -> same stage
    }

    [Fact]
    public void Build_AddEdge_IgnoresSelfAndUnknownEndpoints()
    {
        var empty = new Dictionary<Guid, IReadOnlyList<Guid>>();
        var a = Mr(NewTask(), empty);

        // A self-edge and an edge to an id not in the set must not throw or deadlock.
        var result = ReleasePlanGraph.Build([a], addEdges: [(a.Id, a.Id), (a.Id, Guid.NewGuid())]);

        Assert.Single(result.Stages);
        Assert.NotEqual(-1, StageOf(result, a));
    }

    // ---- repository ordering --------------------------------------------------

    [Fact]
    public void Build_OrdersMergeRequestsByRepositoryDependency_WithinASingleTask()
    {
        // Two merge requests of the SAME task, in two repositories where the backend deploys after
        // the database. Task dependency never orders same-task MRs; the repository policy does.
        var task = NewTask();
        var dbRepo = Guid.NewGuid();
        var backendRepo = Guid.NewGuid();
        var empty = new Dictionary<Guid, IReadOnlyList<Guid>>();

        var dbMr = Mr(task, empty, repositoryId: dbRepo);
        var backendMr = Mr(task, empty, repositoryId: backendRepo,
            repoDependsOn: [new PlanRepositoryLink(dbRepo, StackDependencyType.Hard)]);

        var result = ReleasePlanGraph.Build([dbMr, backendMr]);

        Assert.True(StageOf(result, dbMr) < StageOf(result, backendMr));
    }
}
