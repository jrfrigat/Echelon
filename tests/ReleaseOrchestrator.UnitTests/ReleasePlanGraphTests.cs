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
/// merge request is a few fields — and no longer states, in its own setup, a claim about how EF
/// behaves that it never actually verified. Repository ordering is the mechanism that replaced
/// stacks; the Hard/Soft cycle-breaking rules it inherits are what these exercise.
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

        /// <summary>Its subtasks, which deploy before it. Live for the same reason.</summary>
        public List<Guid> Children { get; } = [];
    }

    /// <summary>A repository under construction, with the repositories it deploys after.</summary>
    private sealed class RepoDef
    {
        public Guid Id { get; } = Guid.NewGuid();
        public List<PlanRepositoryLink> DependsOn { get; } = [];
    }

    private static TaskDef Task() => new();

    /// <summary>Records "dependent depends on dependsOn".</summary>
    private static void DependsOn(TaskDef dependent, TaskDef dependsOn) =>
        dependent.DependsOn.Add(dependsOn.Id);

    /// <summary>Records "child is a subtask of parent", so the child deploys first and the parent last.</summary>
    private static void ChildOf(TaskDef child, TaskDef parent) =>
        parent.Children.Add(child.Id);

    private static RepoDef Repo() => new();

    /// <summary>Records "from depends on to", i.e. every MR in <paramref name="to"/> deploys first.</summary>
    private static void RepoDependsOn(RepoDef from, RepoDef to, StackDependencyType type) =>
        from.DependsOn.Add(new PlanRepositoryLink(to.Id, type));

    private static PlanMergeRequest Mr(TaskDef? task = null, RepoDef? repo = null) =>
        new(Guid.NewGuid(),
            task?.Id,
            task?.DependsOn ?? [],
            task?.Children ?? [],
            repo?.Id ?? Guid.NewGuid(),   // a distinct repository per merge request unless a test shares one
            repo?.DependsOn ?? []);

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

    // ---- task hierarchy -------------------------------------------------------

    /// <summary>
    /// A subtask deploys before the parent it hangs under: the parent is the umbrella, the children
    /// are the concrete work.
    /// </summary>
    /// <remarks>
    /// The parent is listed first in the input on purpose. Input order is what breaks ties inside a
    /// stage, so a hierarchy edge that went missing — or pointed the other way — would leave the
    /// parent in the earlier stage and fail here. Given the child first, the assertion would pass on
    /// tie-break luck whether the edge existed or not.
    /// </remarks>
    [Fact]
    public void SubtaskDeploysBeforeItsParentTask()
    {
        var parent = Task();
        var child = Task();
        ChildOf(child, parent);

        var parentMr = Mr(parent);
        var childMr = Mr(child);

        var result = ReleasePlanGraph.Build([parentMr, childMr]);

        Assert.True(StageOf(result, childMr) < StageOf(result, parentMr));
        Assert.Empty(result.Conflicts);
    }

    /// <summary>A parent spanning several children waits for all of them, not just the first.</summary>
    [Fact]
    public void ParentTaskWaitsForEveryChild()
    {
        var parent = Task();
        var firstChild = Task();
        var secondChild = Task();
        ChildOf(firstChild, parent);
        ChildOf(secondChild, parent);

        var parentMr = Mr(parent);
        var firstMr = Mr(firstChild);
        var secondMr = Mr(secondChild);

        var result = ReleasePlanGraph.Build([parentMr, firstMr, secondMr]);

        Assert.True(StageOf(result, firstMr) < StageOf(result, parentMr));
        Assert.True(StageOf(result, secondMr) < StageOf(result, parentMr));
    }

    /// <summary>
    /// The hierarchy is a mandatory constraint, like a declared dependency: an operator may deploy
    /// against it, but the plan has to say so rather than reorder in silence.
    /// </summary>
    [Fact]
    public void TheParentChildOrderingIsAMandatoryConstraint()
    {
        var parent = Task();
        var child = Task();
        ChildOf(child, parent);

        var parentMr = Mr(parent);
        var childMr = Mr(child);

        var mandatory = ReleasePlanGraph.MandatoryEdges([parentMr, childMr]);

        var edge = Assert.Single(mandatory);
        Assert.Equal(childMr.Id, edge.FromMrId);
        Assert.Equal(parentMr.Id, edge.ToMrId);
        Assert.Equal(PlanEdgeKind.TaskDependency, edge.Kind);
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

    // ---- repository dependencies ----------------------------------------------

    [Fact]
    public void HardRepositoryDependencyOrdersStages()
    {
        var db = Repo();
        var api = Repo();
        RepoDependsOn(api, db, StackDependencyType.Hard);

        var dbMr = Mr(repo: db);
        var apiMr = Mr(repo: api);

        var result = ReleasePlanGraph.Build([apiMr, dbMr]);

        Assert.True(StageOf(result, dbMr) < StageOf(result, apiMr));
    }

    [Fact]
    public void SoftRepositoryDependencyAlsoOrdersWhenNoCycleForcesItOut()
    {
        // README §5.2: soft links are advisory, but honoured when they cost nothing.
        var db = Repo();
        var api = Repo();
        RepoDependsOn(api, db, StackDependencyType.Soft);

        var dbMr = Mr(repo: db);
        var apiMr = Mr(repo: api);

        var result = ReleasePlanGraph.Build([apiMr, dbMr]);

        Assert.True(StageOf(result, dbMr) < StageOf(result, apiMr));
        Assert.Empty(result.Conflicts);
    }

    [Fact]
    public void RepositoryDependingOnItselfDoesNotDeadlock()
    {
        // A repository that (mis)declares a dependency on itself would put both ends of the link on
        // the same MR; a self-edge would strand it. The guard drops it.
        var a = Repo();
        RepoDependsOn(a, a, StackDependencyType.Hard);

        var mr = Mr(repo: a);

        var result = ReleasePlanGraph.Build([mr]);

        Assert.Single(result.Stages);
        Assert.Contains(mr.Id, result.Stages[0]);
        Assert.Empty(result.Conflicts);
    }

    // ---- cycles ---------------------------------------------------------------

    [Fact]
    public void SoftLinkIsSacrificedBeforeTaskLinkToBreakACycle()
    {
        // TASK-2 depends on TASK-1, but A's repository softly depends on B's.
        var t1 = Task();
        var t2 = Task();
        DependsOn(t2, t1);

        var ra = Repo();
        var rb = Repo();
        RepoDependsOn(ra, rb, StackDependencyType.Soft);

        var mrA = Mr(t1, ra);
        var mrB = Mr(t2, rb);

        var result = ReleasePlanGraph.Build([mrA, mrB]);

        var conflict = Assert.Single(result.Conflicts);
        Assert.Equal(PlanEdgeKind.RepoSoft, conflict.DroppedEdgeKind);
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
        var a = Repo();
        var b = Repo();
        RepoDependsOn(a, b, StackDependencyType.Hard);
        RepoDependsOn(b, a, StackDependencyType.Hard);

        var mrA = Mr(repo: a);
        var mrB = Mr(repo: b);

        var result = ReleasePlanGraph.Build([mrA, mrB]);

        var conflict = Assert.Single(result.Conflicts);
        Assert.Equal(PlanEdgeKind.RepoHard, conflict.DroppedEdgeKind);
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
        var a = Repo();
        var b = Repo();
        RepoDependsOn(a, b, StackDependencyType.Soft);

        var edges = ReleasePlanGraph.MandatoryEdges([Mr(repo: a), Mr(repo: b)]);

        Assert.Empty(edges);
    }

    [Fact]
    public void MandatoryEdgesIncludeHardRepositoryLinks()
    {
        var a = Repo();
        var b = Repo();
        RepoDependsOn(a, b, StackDependencyType.Hard);

        var edges = ReleasePlanGraph.MandatoryEdges([Mr(repo: a), Mr(repo: b)]);

        var edge = Assert.Single(edges);
        Assert.Equal(PlanEdgeKind.RepoHard, edge.Kind);
    }

    [Fact]
    public void MandatoryEdgesIncludeTaskLinks()
    {
        var t1 = Task();
        var t2 = Task();
        DependsOn(t2, t1);

        var edges = ReleasePlanGraph.MandatoryEdges([Mr(t1), Mr(t2)]);

        var edge = Assert.Single(edges);
        Assert.Equal(PlanEdgeKind.TaskDependency, edge.Kind);
    }
}
