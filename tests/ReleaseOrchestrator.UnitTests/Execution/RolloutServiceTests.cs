using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using ReleaseOrchestrator.Application.Exceptions;
using ReleaseOrchestrator.Core.Enums;
using ReleaseOrchestrator.Infrastructure.Execution;
using ReleaseOrchestrator.Infrastructure.Persistence.Models;
using ReleaseOrchestrator.Infrastructure.ReleasePlanning;
using ReleaseOrchestrator.UnitTests.ReleasePlanning;
using Xunit;

namespace ReleaseOrchestrator.UnitTests.Execution;

/// <summary>
/// Covers the launch gates and step materialisation over a real (in-memory SQLite) database:
/// the no-plan and readiness gates, wave ordering, already-deployed skipping, the missing-strategy
/// guard, and launch idempotency. The coordinator's external deploy path is not exercised here --
/// it needs a live GitLab.
/// </summary>
public class RolloutServiceTests : PlannerTestBase
{
    private RolloutPlanner Planner_() => new(Db, new FakeTimeProvider(Now), NullLogger<RolloutPlanner>.Instance);

    private RolloutService Service() => new(
        Db, new FakeTimeProvider(Now),
        Options.Create(new RolloutExecutionOptions { EnvironmentProgressionGate = false }),
        NullLogger<RolloutService>.Instance);

    private DeploymentEnvironment AddEnvironment(string key = "prod", int order = 0)
    {
        var env = new DeploymentEnvironment { Id = Guid.NewGuid(), Key = key, Name = key, Order = order, IsEnabled = true };
        Db.DeploymentEnvironments.Add(env);
        return env;
    }

    private void MarkDeployed(Guid mergeRequestId, Guid environmentId) =>
        Db.MrDeploymentStates.Add(new MrDeploymentState
        {
            MergeRequestId = mergeRequestId,
            EnvironmentId = environmentId,
            State = DeploymentState.Deployed,
            UpdatedAt = Now
        });

    [Fact]
    public async Task Launch_Fails_WhenNoPlan()
    {
        var task = AddTask("PROJ-1");
        var env = AddEnvironment();
        await Db.SaveChangesAsync(Ct);

        await Assert.ThrowsAsync<DomainValidationException>(
            () => Service().LaunchAsync(task.Id, env.Id, null, Ct));
    }

    [Fact]
    public async Task Launch_Fails_WhenRepositoryHasNoDeployStrategy()
    {
        var repo = AddRepository("svc"); // no DeployStrategyKey
        var task = AddTask("PROJ-1");
        AddMergeRequest(repo, task);
        var env = AddEnvironment();
        await Db.SaveChangesAsync(Ct);
        await Planner_().RecalculateAsync(task.Id, Ct);

        var ex = await Assert.ThrowsAsync<DomainValidationException>(
            () => Service().LaunchAsync(task.Id, env.Id, null, Ct));
        Assert.Contains("deploy strategy", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Launch_Fails_WhenPrerequisiteNotDeployed()
    {
        var repo = AddRepository("svc");
        repo.DeployStrategyKey = "gitlab-merge";
        var prereq = AddTask("PROJ-1");
        var target = AddTask("PROJ-2");
        AddTaskDependency(target, prereq);
        AddMergeRequest(repo, prereq);
        AddMergeRequest(repo, target);
        var env = AddEnvironment();
        await Db.SaveChangesAsync(Ct);
        await Planner_().RecalculateAsync(target.Id, Ct);

        var ex = await Assert.ThrowsAsync<DomainValidationException>(
            () => Service().LaunchAsync(target.Id, env.Id, null, Ct));
        Assert.Contains("PROJ-1", ex.Message);
    }

    [Fact]
    public async Task Launch_MaterializesSteps_WhenPrerequisiteIsDeployed()
    {
        var repo = AddRepository("svc");
        repo.DeployStrategyKey = "gitlab-merge";
        var prereq = AddTask("PROJ-1");
        var target = AddTask("PROJ-2");
        AddTaskDependency(target, prereq);
        var prereqMr = AddMergeRequest(repo, prereq);
        var targetMr = AddMergeRequest(repo, target);
        var env = AddEnvironment();
        MarkDeployed(prereqMr.Id, env.Id); // prerequisite already deployed in this environment
        await Db.SaveChangesAsync(Ct);
        await Planner_().RecalculateAsync(target.Id, Ct);

        var rollout = await Service().LaunchAsync(target.Id, env.Id, null, Ct);

        Assert.Equal("Running", rollout.Status);
        Assert.Equal(2, rollout.Steps.Count);
        // The already-deployed prerequisite MR is a Skipped step; the target MR is Pending.
        Assert.Equal("Skipped", rollout.Steps.Single(s => s.MergeRequestId == prereqMr.Id).State);
        Assert.Equal("Pending", rollout.Steps.Single(s => s.MergeRequestId == targetMr.Id).State);
        // Prerequisite deploys before the target.
        var prereqWave = rollout.Steps.Single(s => s.MergeRequestId == prereqMr.Id).Wave;
        var targetWave = rollout.Steps.Single(s => s.MergeRequestId == targetMr.Id).Wave;
        Assert.True(prereqWave < targetWave);
    }

    [Fact]
    public async Task Launch_IsIdempotent_ForSamePlanAndEnvironment()
    {
        var repo = AddRepository("svc");
        repo.DeployStrategyKey = "gitlab-merge";
        var task = AddTask("PROJ-1");
        AddMergeRequest(repo, task);
        var env = AddEnvironment();
        await Db.SaveChangesAsync(Ct);
        await Planner_().RecalculateAsync(task.Id, Ct);

        var first = await Service().LaunchAsync(task.Id, env.Id, null, Ct);
        var second = await Service().LaunchAsync(task.Id, env.Id, null, Ct);

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(1, await Db.Rollouts.CountAsync(r => r.TargetTaskId == task.Id, Ct));
    }

    /// <summary>
    /// The idempotency key is unique across all statuses, so once a run for a plan version has
    /// reached a terminal state that version cannot launch to the same environment again. It must be
    /// a clean domain error pointing the operator at recalculation, not the raw unique-constraint
    /// violation surfacing as a 500.
    /// </summary>
    [Fact]
    public async Task Launch_AfterATerminalRun_ReportsACleanConflict()
    {
        var repo = AddRepository("svc");
        repo.DeployStrategyKey = "gitlab-merge";
        var task = AddTask("PROJ-1");
        AddMergeRequest(repo, task);
        var env = AddEnvironment();
        await Db.SaveChangesAsync(Ct);
        await Planner_().RecalculateAsync(task.Id, Ct);

        var first = await Service().LaunchAsync(task.Id, env.Id, null, Ct);

        // Drive the run to a terminal state, as the coordinator would on success or failure.
        var rollout = await Db.Rollouts.FirstAsync(r => r.Id == first.Id, Ct);
        rollout.Status = RolloutStatus.Failed;
        await Db.SaveChangesAsync(Ct);

        var ex = await Assert.ThrowsAsync<DomainValidationException>(
            () => Service().LaunchAsync(task.Id, env.Id, null, Ct));
        Assert.Contains("already been rolled out", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, await Db.Rollouts.CountAsync(r => r.TargetTaskId == task.Id, Ct));
    }
}
