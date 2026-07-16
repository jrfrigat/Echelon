using ReleaseOrchestrator.Application.ReleasePlanning;
using ReleaseOrchestrator.Core.Entities;
using ReleaseOrchestrator.Core.Enums;
using Xunit;

namespace ReleaseOrchestrator.UnitTests;

/// <summary>
/// Covers the ordering rules the product exists to enforce. The regression guarded by
/// <see cref="PredecessorTaskDeploysBeforeDependentTask"/> shipped undetected because
/// the algorithm previously could not be exercised without a database.
/// </summary>
public class ReleasePlanGraphTests
{
    // ---- builders -------------------------------------------------------------

    private static TaskItem Task(string key) => new()
    {
        Id = Guid.NewGuid(),
        ExternalId = key,
        TrackerConnectionId = Guid.NewGuid()
    };

    /// <summary>Records "dependent depends on dependsOn", wiring both navigations the way EF does.</summary>
    private static void DependsOn(TaskItem dependent, TaskItem dependsOn)
    {
        var link = new TaskDependency
        {
            Id = Guid.NewGuid(),
            DependentTaskId = dependent.Id,
            DependsOnTaskId = dependsOn.Id,
            DependentTask = dependent,
            DependsOnTask = dependsOn
        };

        dependent.Dependencies.Add(link);
        dependsOn.Dependents.Add(link);
    }

    private static Stack Stack(string name) => new() { Id = Guid.NewGuid(), Name = name };

    /// <summary>Records "from depends on to", i.e. every MR in <paramref name="to"/> deploys first.</summary>
    private static void StackDependsOn(Stack from, Stack to, StackDependencyType type)
    {
        var link = new StackDependency
        {
            Id = Guid.NewGuid(),
            FromStackId = from.Id,
            ToStackId = to.Id,
            Type = type,
            FromStack = from,
            ToStack = to
        };

        from.DependentOn.Add(link);
        to.RequiredBy.Add(link);
    }

    private static MergeRequest Mr(string externalId, TaskItem? task = null, params Stack[] stacks)
    {
        var repo = new Repository
        {
            Id = Guid.NewGuid(),
            Name = $"repo-{externalId}",
            ExternalId = $"group/repo-{externalId}",
            ConnectionId = Guid.NewGuid()
        };

        foreach (var stack in stacks)
        {
            var rs = new RepositoryStack { RepositoryId = repo.Id, StackId = stack.Id, Repository = repo, Stack = stack };
            repo.RepositoryStacks.Add(rs);
            stack.RepositoryStacks.Add(rs);
        }

        var mr = new MergeRequest
        {
            Id = Guid.NewGuid(),
            ExternalId = externalId,
            SourceBranch = $"feature/{externalId}",
            TargetBranch = "main",
            RepositoryId = repo.Id,
            Repository = repo,
            Status = MergeRequestStatus.ReadyForDeploy,
            TaskId = task?.Id,
            Task = task
        };

        task?.MergeRequests.Add(mr);
        return mr;
    }

    private static int StageOf(PlanGraphResult result, MergeRequest mr) =>
        result.Stages.FindIndex(stage => stage.Contains(mr.Id));

    // ---- task dependencies ----------------------------------------------------

    [Fact]
    public void PredecessorTaskDeploysBeforeDependentTask()
    {
        var first = Task("TASK-1");
        var second = Task("TASK-2");
        DependsOn(second, first);

        var mrA = Mr("A", first);
        var mrB = Mr("B", second);

        var result = ReleasePlanGraph.Build([mrA, mrB]);

        Assert.True(StageOf(result, mrA) < StageOf(result, mrB));
        Assert.Empty(result.Conflicts);
    }

    [Fact]
    public void EveryMergeRequestOfAPredecessorTaskDeploysFirst()
    {
        // One task commonly spans several repositories; all of its MRs are prerequisites.
        var first = Task("TASK-1");
        var second = Task("TASK-2");
        DependsOn(second, first);

        var mrA1 = Mr("A1", first);
        var mrA2 = Mr("A2", first);
        var mrB = Mr("B", second);

        var result = ReleasePlanGraph.Build([mrA1, mrA2, mrB]);

        Assert.True(StageOf(result, mrA1) < StageOf(result, mrB));
        Assert.True(StageOf(result, mrA2) < StageOf(result, mrB));
    }

    [Fact]
    public void IndependentMergeRequestsShareOneStage()
    {
        var result = ReleasePlanGraph.Build([Mr("A"), Mr("B"), Mr("C")]);

        Assert.Single(result.Stages);
        Assert.Equal(3, result.Stages[0].Count);
    }

    [Fact]
    public void ChainOfThreeTasksProducesThreeStages()
    {
        var t1 = Task("TASK-1");
        var t2 = Task("TASK-2");
        var t3 = Task("TASK-3");
        DependsOn(t2, t1);
        DependsOn(t3, t2);

        var result = ReleasePlanGraph.Build([Mr("C", t3), Mr("B", t2), Mr("A", t1)]);

        Assert.Equal(3, result.Stages.Count);
    }

    // ---- stack dependencies ---------------------------------------------------

    [Fact]
    public void HardStackDependencyOrdersStages()
    {
        var db = Stack("db");
        var api = Stack("api");
        StackDependsOn(api, db, StackDependencyType.Hard);

        var dbMr = Mr("db-1", null, db);
        var apiMr = Mr("api-1", null, api);

        var result = ReleasePlanGraph.Build([apiMr, dbMr]);

        Assert.True(StageOf(result, dbMr) < StageOf(result, apiMr));
    }

    [Fact]
    public void SoftStackDependencyAlsoOrdersWhenNoCycleForcesItOut()
    {
        // README §5.2: soft links are advisory, but honoured when they cost nothing.
        var db = Stack("db");
        var api = Stack("api");
        StackDependsOn(api, db, StackDependencyType.Soft);

        var dbMr = Mr("db-1", null, db);
        var apiMr = Mr("api-1", null, api);

        var result = ReleasePlanGraph.Build([apiMr, dbMr]);

        Assert.True(StageOf(result, dbMr) < StageOf(result, apiMr));
        Assert.Empty(result.Conflicts);
    }

    [Fact]
    public void RepositoryInTwoMutuallyDependentStacksDoesNotDeadlock()
    {
        // Both ends of the link resolve to the same MR; a self-edge would strand it.
        var a = Stack("a");
        var b = Stack("b");
        StackDependsOn(a, b, StackDependencyType.Hard);

        var mr = Mr("both", null, a, b);

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
        var t1 = Task("TASK-1");
        var t2 = Task("TASK-2");
        DependsOn(t2, t1);

        var sa = Stack("sa");
        var sb = Stack("sb");
        StackDependsOn(sa, sb, StackDependencyType.Soft);

        var mrA = Mr("A", t1, sa);
        var mrB = Mr("B", t2, sb);

        var result = ReleasePlanGraph.Build([mrA, mrB]);

        var conflict = Assert.Single(result.Conflicts);
        Assert.Equal(PlanEdgeKind.StackSoft, conflict.DroppedEdgeKind);
        // The task link survives, so the tracker's ordering still holds.
        Assert.True(StageOf(result, mrA) < StageOf(result, mrB));
    }

    [Fact]
    public void CyclicTasksStillProduceAPlanAndAreReported()
    {
        var t1 = Task("TASK-1");
        var t2 = Task("TASK-2");
        DependsOn(t2, t1);
        DependsOn(t1, t2);

        var mrA = Mr("A", t1);
        var mrB = Mr("B", t2);

        var result = ReleasePlanGraph.Build([mrA, mrB]);

        Assert.Single(result.Conflicts);
        Assert.Equal(2, result.Stages.Sum(s => s.Count));
    }

    [Fact]
    public void CycleDoesNotDragUnrelatedDownstreamMergeRequestsIntoALastStage()
    {
        // The old planner appended the cycle *and everything reachable from it* to a
        // single trailing stage, silently discarding their ordering.
        var t1 = Task("TASK-1");
        var t2 = Task("TASK-2");
        var t3 = Task("TASK-3");
        DependsOn(t2, t1);
        DependsOn(t1, t2);   // cycle between 1 and 2
        DependsOn(t3, t2);   // 3 legitimately follows 2

        var mrA = Mr("A", t1);
        var mrB = Mr("B", t2);
        var mrC = Mr("C", t3);

        var result = ReleasePlanGraph.Build([mrA, mrB, mrC]);

        Assert.True(StageOf(result, mrB) < StageOf(result, mrC));
        Assert.Equal(3, result.Stages.Sum(s => s.Count));
    }

    [Fact]
    public void AllHardCycleIsReportedAsUnresolvable()
    {
        var a = Stack("a");
        var b = Stack("b");
        StackDependsOn(a, b, StackDependencyType.Hard);
        StackDependsOn(b, a, StackDependencyType.Hard);

        var mrA = Mr("A", null, a);
        var mrB = Mr("B", null, b);

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
        var t1 = Task("TASK-1");
        var t2 = Task("TASK-2");
        DependsOn(t2, t1);

        var mrs = new List<MergeRequest> { Mr("A", t1), Mr("B", t2), Mr("C") };

        var first = ReleasePlanGraph.Build(mrs);
        var second = ReleasePlanGraph.Build(mrs);

        Assert.Equal(
            first.Stages.Select(s => s.ToList()).ToList(),
            second.Stages.Select(s => s.ToList()).ToList());
    }

    [Fact]
    public void MergeRequestWithoutATaskIsStillPlanned()
    {
        var result = ReleasePlanGraph.Build([Mr("no-task")]);

        Assert.Single(result.Stages);
    }

    [Fact]
    public void DependencyOnATaskWithNoDeployableMergeRequestIsIgnored()
    {
        // TASK-1 has nothing to deploy, so it cannot constrain TASK-2.
        var t1 = Task("TASK-1");
        var t2 = Task("TASK-2");
        DependsOn(t2, t1);

        var result = ReleasePlanGraph.Build([Mr("B", t2)]);

        Assert.Single(result.Stages);
        Assert.Empty(result.Conflicts);
    }

    [Fact]
    public void MandatoryEdgesExcludeSoftLinks()
    {
        var db = Stack("db");
        var api = Stack("api");
        StackDependsOn(api, db, StackDependencyType.Soft);

        var edges = ReleasePlanGraph.MandatoryEdges([Mr("api-1", null, api), Mr("db-1", null, db)]);

        Assert.Empty(edges);
    }

    [Fact]
    public void MandatoryEdgesIncludeHardAndTaskLinks()
    {
        var t1 = Task("TASK-1");
        var t2 = Task("TASK-2");
        DependsOn(t2, t1);

        var edges = ReleasePlanGraph.MandatoryEdges([Mr("A", t1), Mr("B", t2)]);

        var edge = Assert.Single(edges);
        Assert.Equal(PlanEdgeKind.TaskDependency, edge.Kind);
    }
}
