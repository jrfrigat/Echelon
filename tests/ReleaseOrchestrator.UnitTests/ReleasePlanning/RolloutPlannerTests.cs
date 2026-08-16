using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using ReleaseOrchestrator.Infrastructure.ReleasePlanning;
using Xunit;

namespace ReleaseOrchestrator.UnitTests.ReleasePlanning;

/// <summary>
/// Covers the per-task planner over a real (in-memory SQLite) database: closure extraction, wave
/// ordering, and the one-active-plan-per-task invariant.
/// </summary>
public class RolloutPlannerTests : PlannerTestBase
{
    private RolloutPlanner Rollout() =>
        new(Db, new FakeTimeProvider(Now), NullLogger<RolloutPlanner>.Instance);

    [Fact]
    public async Task GetActivePlan_IsNull_BeforeAnyBuild()
    {
        var task = AddTask("PROJ-1");
        await Db.SaveChangesAsync(Ct);

        Assert.Null(await Rollout().GetActivePlanAsync(task.Id, Ct));
    }

    [Fact]
    public async Task Recalculate_BuildsClosure_AndOrdersPrerequisiteFirst()
    {
        var repo = AddRepository("svc");
        var prereq = AddTask("PROJ-1");
        var target = AddTask("PROJ-2");
        AddTaskDependency(target, prereq);
        AddMergeRequest(repo, prereq);
        AddMergeRequest(repo, target);
        await Db.SaveChangesAsync(Ct);

        var plan = await Rollout().RecalculateAsync(target.Id, actor: null, Ct);

        Assert.True(plan.IsActive);
        Assert.Equal(2, plan.Nodes.Count);                       // target + prerequisite
        Assert.Contains(plan.Nodes, n => n.IsTarget && n.TaskKey == "PROJ-2");

        var prereqWave = plan.Nodes.Single(n => n.TaskKey == "PROJ-1").Items.Single().Wave;
        var targetWave = plan.Nodes.Single(n => n.TaskKey == "PROJ-2").Items.Single().Wave;
        Assert.True(prereqWave < targetWave);
    }

    [Fact]
    public async Task Recalculate_ExcludesTasksOutsideTheClosure()
    {
        var repo = AddRepository("svc");
        var target = AddTask("PROJ-1");
        var unrelated = AddTask("PROJ-9");
        AddMergeRequest(repo, target);
        AddMergeRequest(repo, unrelated);
        await Db.SaveChangesAsync(Ct);

        var plan = await Rollout().RecalculateAsync(target.Id, actor: null, Ct);

        Assert.Single(plan.Nodes);
        Assert.Equal("PROJ-1", plan.Nodes[0].TaskKey);
    }

    /// <summary>
    /// Rolling out a parent pulls its subtasks in and deploys them first.
    /// </summary>
    /// <remarks>
    /// Worth running against a database rather than only in <c>ReleasePlanGraphTests</c>, because the
    /// hierarchy has to survive two independent reads that can disagree: the adjacency decides which
    /// tasks are in the closure at all, and the projection decides the edges between their merge
    /// requests. Miss it in the adjacency and the child is not in the plan; miss it in the projection
    /// and the child is in the plan but in the same wave as its parent. Both look like a plan.
    /// </remarks>
    [Fact]
    public async Task Recalculate_IncludesSubtasks_AndDeploysThemBeforeTheParent()
    {
        var repo = AddRepository("svc");
        var parent = AddTask("PROJ-1");
        var child = AddTask("PROJ-2");
        AddChild(parent, child);
        AddMergeRequest(repo, parent);
        AddMergeRequest(repo, child);
        await Db.SaveChangesAsync(Ct);

        var plan = await Rollout().RecalculateAsync(parent.Id, actor: null, Ct);

        Assert.Equal(2, plan.Nodes.Count);                       // parent + subtask
        Assert.Contains(plan.Nodes, n => n.IsTarget && n.TaskKey == "PROJ-1");

        var childWave = plan.Nodes.Single(n => n.TaskKey == "PROJ-2").Items.Single().Wave;
        var parentWave = plan.Nodes.Single(n => n.TaskKey == "PROJ-1").Items.Single().Wave;
        Assert.True(childWave < parentWave);
    }

    /// <summary>
    /// The hierarchy points one way: a subtask's own rollout is just the subtask. Rolling out a child
    /// must not drag in the parent (and, through it, every sibling).
    /// </summary>
    [Fact]
    public async Task Recalculate_OfASubtask_DoesNotPullInItsParent()
    {
        var repo = AddRepository("svc");
        var parent = AddTask("PROJ-1");
        var child = AddTask("PROJ-2");
        var sibling = AddTask("PROJ-3");
        AddChild(parent, child);
        AddChild(parent, sibling);
        AddMergeRequest(repo, parent);
        AddMergeRequest(repo, child);
        AddMergeRequest(repo, sibling);
        await Db.SaveChangesAsync(Ct);

        var plan = await Rollout().RecalculateAsync(child.Id, actor: null, Ct);

        Assert.Single(plan.Nodes);
        Assert.Equal("PROJ-2", plan.Nodes[0].TaskKey);
    }

    /// <summary>
    /// The hierarchy reads from both ends, and it is readable with no plan built - which is the
    /// point: a subtask's parent is never in the subtask's own plan tree, because a task does not
    /// wait on its parent.
    /// </summary>
    [Fact]
    public async Task GetTask_ReportsTheParentAndTheSubtasks_WithoutAPlan()
    {
        var parent = AddTask("EPIC-1");
        var firstChild = AddTask("PROJ-3");
        var secondChild = AddTask("PROJ-2");
        AddChild(parent, firstChild);
        AddChild(parent, secondChild);
        await Db.SaveChangesAsync(Ct);

        var fromParent = await Rollout().GetTaskAsync(parent.Id, Ct);
        Assert.NotNull(fromParent);
        Assert.Null(fromParent!.Parent);
        Assert.Equal(["PROJ-2", "PROJ-3"], fromParent.Children.Select(c => c.ExternalId));

        var fromChild = await Rollout().GetTaskAsync(firstChild.Id, Ct);
        Assert.NotNull(fromChild);
        Assert.Equal("EPIC-1", fromChild!.Parent?.ExternalId);
        Assert.Empty(fromChild.Children);
    }

    [Fact]
    public async Task GetTask_IsNull_ForATaskThatDoesNotExist() =>
        Assert.Null(await Rollout().GetTaskAsync(Guid.NewGuid(), Ct));

    [Fact]
    public async Task Recalculate_KeepsOneActivePlanPerTask()
    {
        var task = AddTask("PROJ-1");
        await Db.SaveChangesAsync(Ct);

        await Rollout().RecalculateAsync(task.Id, actor: null, Ct);
        await Rollout().RecalculateAsync(task.Id, actor: null, Ct);

        var active = await Db.RolloutPlans.CountAsync(p => p.TargetTaskId == task.Id && p.IsActive, Ct);
        Assert.Equal(1, active);
    }
}
