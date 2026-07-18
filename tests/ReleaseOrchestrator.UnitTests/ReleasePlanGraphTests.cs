using ReleaseOrchestrator.Application.ReleasePlanning;
using ReleaseOrchestrator.Core.Enums;
using Xunit;

namespace ReleaseOrchestrator.UnitTests;

/// <summary>
/// Covers the ordering rules the product exists to enforce. The regression guarded by
/// <see cref="PredecessorTaskDeploysBeforeDependentTask"/> shipped undetected because
/// the algorithm previously could not be exercised without a database.
/// </summary>
/// <remarks>
/// These builders used to construct entity graphs and wire both ends of every navigation by hand,
/// because the algorithm read <c>MergeRequest.Task.Dependencies</c> and the test had to imitate
/// EF's fixup to reach it. The algorithm now takes what it needs and nothing else, so a test
/// merge request is three fields — and no longer states, in its own setup, a claim about how EF
/// behaves that it never actually verified.
/// </remarks>
public class ReleasePlanGraphTests
{
    // ---- builders -------------------------------------------------------------

    /// <summary>A task under construction. Only its identity and what it waits on matter here.</summary>
    private sealed class TaskDef
    {
        public Guid Id { get; } = Guid.NewGuid();

        /// <summary>Live, so a test may declare dependencies before or after building the MR.</summary>
        public List<Guid> DependsOn { get; } = [];
    }

    private sealed class StackDef
    {
        public Guid Id { get; } = Guid.NewGuid();
        public List<PlanStackLink> DependsOn { get; } = [];
    }

    private static TaskDef Task() => new();

    /// <summary>Records "dependent depends on dependsOn".</summary>
    private static void DependsOn(TaskDef dependent, TaskDef dependsOn) =>
        dependent.DependsOn.Add(dependsOn.Id);

    private static StackDef Stack() => new();

    /// <summary>Records "from depends on to", i.e. every MR in <paramref name="to"/> deploys first.</summary>
    private static void StackDependsOn(StackDef from, StackDef to, StackDependencyType type) =>
        from.DependsOn.Add(new PlanStackLink(to.Id, type));

    private static PlanMergeRequest Mr(TaskDef? task = null, params StackDef[] stacks) =>
        new(Guid.NewGuid(),
            task?.Id,
            task?.DependsOn ?? [],
            [.. stacks.Select(s => new PlanRepositoryStack(s.Id, s.DependsOn))],
            Guid.NewGuid(),   // a distinct repository per merge request unless a test says otherwise
            []);

    private static int StageOf(PlanGraphResult result, PlanMergeRequest mr) =>
        result.Stages.FindIndex(stage => stage.Contains(mr.Id));

    // ---- task dependencies ----------------------------------------------------

    [Fact]
    public void PredecessorTaskDeploysBeforeDependentTask()
    {
        var first = Task();
        var second = Task();
        DependsOn(second, first);

        var mrA = Mr(first);
        var mrB = Mr(second);

        var result = ReleasePlanGraph.Build([mrA, mrB]);

        Assert.True(StageOf(result, mrA) < StageOf(result, mrB));
        Assert.Empty(result.Conflicts);
    }

    [Fact]
    public void EveryMergeRequestOfAPredecessorTaskDeploysFirst()
    {
        // One task commonly spans several repositories; all of its MRs are prerequisites.
        var first = Task();
        var second = Task();
        DependsOn(second, first);

        var mrA1 = Mr(first);
        var mrA2 = Mr(first);
        var mrB = Mr(second);

        var result = ReleasePlanGraph.Build([mrA1, mrA2, mrB]);

        Assert.True(StageOf(result, mrA1) < StageOf(result, mrB));
        Assert.True(StageOf(result, mrA2) < StageOf(result, mrB));
    }

    [Fact]
    public void IndependentMergeRequestsShareOneStage()
    {
        var result = ReleasePlanGraph.Build([Mr(), Mr(), Mr()]);

        Assert.Single(result.Stages);
        Assert.Equal(3, result.Stages[0].Count);
    }

    [Fact]
    public void ChainOfThreeTasksProducesThreeStages()
    {
        var t1 = Task();
        var t2 = Task();
        var t3 = Task();
        DependsOn(t2, t1);
        DependsOn(t3, t2);

        var result = ReleasePlanGraph.Build([Mr(t3), Mr(t2), Mr(t1)]);

        Assert.Equal(3, result.Stages.Count);
    }

    // ---- stack dependencies ---------------------------------------------------

    [Fact]
    public void HardStackDependencyOrdersStages()
    {
        var db = Stack();
        var api = Stack();
        StackDependsOn(api, db, StackDependencyType.Hard);

        var dbMr = Mr(null, db);
        var apiMr = Mr(null, api);

        var result = ReleasePlanGraph.Build([apiMr, dbMr]);

        Assert.True(StageOf(result, dbMr) < StageOf(result, apiMr));
    }

    [Fact]
    public void SoftStackDependencyAlsoOrdersWhenNoCycleForcesItOut()
    {
        // README §5.2: soft links are advisory, but honoured when they cost nothing.
        var db = Stack();
        var api = Stack();
        StackDependsOn(api, db, StackDependencyType.Soft);

        var dbMr = Mr(null, db);
        var apiMr = Mr(null, api);

        var result = ReleasePlanGraph.Build([apiMr, dbMr]);

        Assert.True(StageOf(result, dbMr) < StageOf(result, apiMr));
        Assert.Empty(result.Conflicts);
    }

    [Fact]
    public void RepositoryInTwoMutuallyDependentStacksDoesNotDeadlock()
    {
        // Both ends of the link resolve to the same MR; a self-edge would strand it.
        var a = Stack();
        var b = Stack();
        StackDependsOn(a, b, StackDependencyType.Hard);

        var mr = Mr(null, a, b);

        var result = ReleasePlanGraph.Build([mr]);

        Assert.Single(result.Stages);
        Assert.Contains(mr.Id, result.Stages[0]);
        Assert.Empty(result.Conflicts);
    }

    // ---- cycles ---------------------------------------------------------------

    [Fact]
    public void SoftLinkIsSacrificedBeforeTaskLinkToBreakACycle()
    {
        // TASK-2 depends on TASK-1, but B's stack softly depends on A's stack.
        var t1 = Task();
        var t2 = Task();
        DependsOn(t2, t1);

        var sa = Stack();
        var sb = Stack();
        StackDependsOn(sa, sb, StackDependencyType.Soft);

        var mrA = Mr(t1, sa);
        var mrB = Mr(t2, sb);

        var result = ReleasePlanGraph.Build([mrA, mrB]);

        var conflict = Assert.Single(result.Conflicts);
        Assert.Equal(PlanEdgeKind.StackSoft, conflict.DroppedEdgeKind);
        // The task link survives, so the tracker's ordering still holds.
        Assert.True(StageOf(result, mrA) < StageOf(result, mrB));
    }

    [Fact]
    public void CyclicTasksStillProduceAPlanAndAreReported()
    {
        var t1 = Task();
        var t2 = Task();
        DependsOn(t2, t1);
        DependsOn(t1, t2);

        var mrA = Mr(t1);
        var mrB = Mr(t2);

        var result = ReleasePlanGraph.Build([mrA, mrB]);

        Assert.Single(result.Conflicts);
        Assert.Equal(2, result.Stages.Sum(s => s.Count));
    }

    [Fact]
    public void CycleDoesNotDragUnrelatedDownstreamMergeRequestsIntoALastStage()
    {
        // The old planner appended the cycle *and everything reachable from it* to a
        // single trailing stage, silently discarding their ordering.
        var t1 = Task();
        var t2 = Task();
        var t3 = Task();
        DependsOn(t2, t1);
        DependsOn(t1, t2);   // cycle between 1 and 2
        DependsOn(t3, t2);   // 3 legitimately follows 2

        var mrA = Mr(t1);
        var mrB = Mr(t2);
        var mrC = Mr(t3);

        var result = ReleasePlanGraph.Build([mrA, mrB, mrC]);

        Assert.True(StageOf(result, mrB) < StageOf(result, mrC));
        Assert.Equal(3, result.Stages.Sum(s => s.Count));
    }

    [Fact]
    public void AllHardCycleIsReportedAsUnresolvable()
    {
        var a = Stack();
        var b = Stack();
        StackDependsOn(a, b, StackDependencyType.Hard);
        StackDependsOn(b, a, StackDependencyType.Hard);

        var mrA = Mr(null, a);
        var mrB = Mr(null, b);

        var result = ReleasePlanGraph.Build([mrA, mrB]);

        var conflict = Assert.Single(result.Conflicts);
        Assert.Equal(PlanEdgeKind.StackHard, conflict.DroppedEdgeKind);
        Assert.Contains("cannot be satisfied", conflict.Reason);
    }

    // ---- determinism and edge cases -------------------------------------------

    [Fact]
    public void EmptyInputProducesNoStages()
    {
        var result = ReleasePlanGraph.Build([]);

        Assert.Empty(result.Stages);
        Assert.Empty(result.Conflicts);
    }

    [Fact]
    public void SameInputProducesIdenticalPlan()
    {
        var t1 = Task();
        var t2 = Task();
        DependsOn(t2, t1);

        var mrs = new List<PlanMergeRequest> { Mr(t1), Mr(t2), Mr() };

        var first = ReleasePlanGraph.Build(mrs);
        var second = ReleasePlanGraph.Build(mrs);

        Assert.Equal(
            first.Stages.Select(s => s.ToList()).ToList(),
            second.Stages.Select(s => s.ToList()).ToList());
    }

    [Fact]
    public void MergeRequestWithoutATaskIsStillPlanned()
    {
        var result = ReleasePlanGraph.Build([Mr()]);

        Assert.Single(result.Stages);
    }

    [Fact]
    public void DependencyOnATaskWithNoDeployableMergeRequestIsIgnored()
    {
        // TASK-1 has nothing to deploy, so it cannot constrain TASK-2.
        var t1 = Task();
        var t2 = Task();
        DependsOn(t2, t1);

        var result = ReleasePlanGraph.Build([Mr(t2)]);

        Assert.Single(result.Stages);
        Assert.Empty(result.Conflicts);
    }

    [Fact]
    public void MandatoryEdgesExcludeSoftLinks()
    {
        var db = Stack();
        var api = Stack();
        StackDependsOn(api, db, StackDependencyType.Soft);

        var edges = ReleasePlanGraph.MandatoryEdges([Mr(null, api), Mr(null, db)]);

        Assert.Empty(edges);
    }

    [Fact]
    public void MandatoryEdgesIncludeHardAndTaskLinks()
    {
        var t1 = Task();
        var t2 = Task();
        DependsOn(t2, t1);

        var edges = ReleasePlanGraph.MandatoryEdges([Mr(t1), Mr(t2)]);

        var edge = Assert.Single(edges);
        Assert.Equal(PlanEdgeKind.TaskDependency, edge.Kind);
    }
}
