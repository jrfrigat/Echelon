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

        var plan = await Rollout().RecalculateAsync(target.Id, Ct);

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

        var plan = await Rollout().RecalculateAsync(target.Id, Ct);

        Assert.Single(plan.Nodes);
        Assert.Equal("PROJ-1", plan.Nodes[0].TaskKey);
    }

    [Fact]
    public async Task Recalculate_KeepsOneActivePlanPerTask()
    {
        var task = AddTask("PROJ-1");
        await Db.SaveChangesAsync(Ct);

        await Rollout().RecalculateAsync(task.Id, Ct);
        await Rollout().RecalculateAsync(task.Id, Ct);

        var active = await Db.RolloutPlans.CountAsync(p => p.TargetTaskId == task.Id && p.IsActive, Ct);
        Assert.Equal(1, active);
    }
}
