using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using ReleaseOrchestrator.Application.DTOs;
using ReleaseOrchestrator.Application.Exceptions;
using ReleaseOrchestrator.Core.Enums;
using ReleaseOrchestrator.Core.Parsing;
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

    private RepositoryDeployTarget AddDeployTarget(
        Repository repo, DeploymentEnvironment env, string strategyKey,
        string? settingsJson = null, RedeployPolicy policy = RedeployPolicy.Once)
    {
        var target = new RepositoryDeployTarget
        {
            Id = Guid.NewGuid(),
            RepositoryId = repo.Id,
            EnvironmentId = env.Id,
            DeployStrategyKey = strategyKey,
            DeploySettingsJson = settingsJson,
            RedeployPolicy = policy
        };
        Db.RepositoryDeployTargets.Add(target);
        return target;
    }

    private Task<RolloutStep> StepAsync(Guid rolloutId) =>
        Db.RolloutSteps.AsNoTracking().FirstAsync(s => s.RolloutId == rolloutId, Ct);

    private DeploymentEnvironment AddGatedEnvironment(string key, ReadyRule mode, string readyLabels)
    {
        var env = AddEnvironment(key);
        // Readiness is a named rule now, not inline columns: build one whose required signals are the
        // given labels as label: tokens -- the same set the old inline label gate compared against.
        var required = string.Join(',', readyLabels
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(ReadinessSignals.Label));
        var rule = new ReadinessRule
        {
            Id = Guid.NewGuid(),
            Name = $"{key}-rule",
            Mode = mode,
            RequiredSignals = required
        };
        Db.ReadinessRules.Add(rule);
        env.ReadinessRuleId = rule.Id;
        return env;
    }

    private void AddPin(Guid mergeRequestId, Guid environmentId, bool isReady) =>
        Db.MergeRequestReadinessPins.Add(new MergeRequestReadinessPin
        {
            Id = Guid.NewGuid(),
            MergeRequestId = mergeRequestId,
            EnvironmentId = environmentId,
            IsReady = isReady,
            At = Now
        });

    [Fact]
    public async Task Launch_Fails_WhenNoPlan()
    {
        var task = AddTask("PROJ-1");
        var env = AddEnvironment();
        await Db.SaveChangesAsync(Ct);

        await Assert.ThrowsAsync<DomainValidationException>(
            () => Service().LaunchAsync(task.Id, env.Id, ActorRef.System, ct: Ct));
    }

    [Fact]
    public async Task Launch_Fails_WhenRepositoryHasNoDeployStrategy()
    {
        var repo = AddRepository("svc"); // no DeployStrategyKey
        var task = AddTask("PROJ-1");
        AddMergeRequest(repo, task);
        var env = AddEnvironment();
        await Db.SaveChangesAsync(Ct);
        await Planner_().RecalculateAsync(task.Id, actor: null, Ct);

        var ex = await Assert.ThrowsAsync<DomainValidationException>(
            () => Service().LaunchAsync(task.Id, env.Id, ActorRef.System, ct: Ct));
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
        await Planner_().RecalculateAsync(target.Id, actor: null, Ct);

        var ex = await Assert.ThrowsAsync<DomainValidationException>(
            () => Service().LaunchAsync(target.Id, env.Id, ActorRef.System, ct: Ct));
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
        await Planner_().RecalculateAsync(target.Id, actor: null, Ct);

        var rollout = await Service().LaunchAsync(target.Id, env.Id, ActorRef.System, ct: Ct);

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
        await Planner_().RecalculateAsync(task.Id, actor: null, Ct);

        var first = await Service().LaunchAsync(task.Id, env.Id, ActorRef.System, ct: Ct);
        var second = await Service().LaunchAsync(task.Id, env.Id, ActorRef.System, ct: Ct);

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(1, await Db.Rollouts.CountAsync(r => r.TargetTaskId == task.Id, Ct));
    }

    /// <summary>
    /// A recalculation between two launches must not start a second concurrent run. The idempotency
    /// key embeds the plan version, which recalculation rotates on every ingestion event, so a second
    /// launch gets a fresh key — and a liveness check on the key alone would treat it as a brand-new
    /// run. Liveness is per (task, environment): the still-running first rollout blocks it.
    /// </summary>
    [Fact]
    public async Task Launch_WhileAnEarlierRunIsStillLive_IsRefused_EvenAfterARecalculation()
    {
        var repo = AddRepository("svc");
        repo.DeployStrategyKey = "gitlab-merge";
        var task = AddTask("PROJ-1");
        AddMergeRequest(repo, task);
        var env = AddEnvironment();
        await Db.SaveChangesAsync(Ct);
        await Planner_().RecalculateAsync(task.Id, actor: null, Ct);

        var first = await Service().LaunchAsync(task.Id, env.Id, ActorRef.System, ct: Ct);
        Assert.Equal("Running", first.Status);

        // A new ingestion event recalculates the plan, minting a new plan id and so a new key.
        await Planner_().RecalculateAsync(task.Id, actor: null, Ct);

        var ex = await Assert.ThrowsAsync<DomainValidationException>(
            () => Service().LaunchAsync(task.Id, env.Id, ActorRef.System, ct: Ct));
        Assert.Contains("already running", ex.Message, StringComparison.OrdinalIgnoreCase);
        // Still exactly one rollout: the second launch created nothing.
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
        await Planner_().RecalculateAsync(task.Id, actor: null, Ct);

        var first = await Service().LaunchAsync(task.Id, env.Id, ActorRef.System, ct: Ct);

        // Drive the run to a terminal state, as the coordinator would on success or failure.
        var rollout = await Db.Rollouts.FirstAsync(r => r.Id == first.Id, Ct);
        rollout.Status = RolloutStatus.Failed;
        await Db.SaveChangesAsync(Ct);

        var ex = await Assert.ThrowsAsync<DomainValidationException>(
            () => Service().LaunchAsync(task.Id, env.Id, ActorRef.System, ct: Ct));
        Assert.Contains("already been rolled out", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, await Db.Rollouts.CountAsync(r => r.TargetTaskId == task.Id, Ct));
    }

    // ---- per-environment deploy targets (E4) ----------------------------------

    /// <summary>
    /// The same repository deploys differently per environment: a target for this environment
    /// overrides the repository's default strategy, which is how prod merges but a test rig triggers
    /// a pipeline.
    /// </summary>
    [Fact]
    public async Task Launch_PrefersThePerEnvironmentTarget_OverTheRepositoryDefault()
    {
        var repo = AddRepository("svc");
        repo.DeployStrategyKey = "gitlab-merge";               // repository default
        var task = AddTask("PROJ-1");
        AddMergeRequest(repo, task);
        var env = AddEnvironment("test");
        AddDeployTarget(repo, env, "gitlab-pipeline", settingsJson: """{"stage":"deploy-test"}""");
        await Db.SaveChangesAsync(Ct);
        await Planner_().RecalculateAsync(task.Id, actor: null, Ct);

        var rollout = await Service().LaunchAsync(task.Id, env.Id, ActorRef.System, ct: Ct);

        var step = await StepAsync(rollout.Id);
        Assert.Equal("gitlab-pipeline", step.DeployStrategyKey);
        // The target's settings are frozen onto the step, not left to be re-read from the repository.
        Assert.Equal("""{"stage":"deploy-test"}""", step.DeploySettingsJson);
    }

    /// <summary>
    /// A target that supplies the key but has no settings does NOT inherit the repository's settings.
    /// Those were saved for the repository's own (different) strategy, and freezing them onto a step
    /// whose key is the target's would hand the strategy another strategy's settings — and decrypt its
    /// secrets under the wrong schema at dispatch. The key's source and the settings' source must match.
    /// </summary>
    [Fact]
    public async Task Launch_DoesNotBorrowRepositorySettings_WhenTheTargetSuppliesTheKey()
    {
        var repo = AddRepository("svc");
        repo.DeployStrategyKey = "gitlab-merge";
        repo.DeployStrategySettingsJson = """{"squash":"true"}""";     // for gitlab-merge, not the target's strategy
        var task = AddTask("PROJ-1");
        AddMergeRequest(repo, task);
        var env = AddEnvironment("test");
        AddDeployTarget(repo, env, "gitlab-pipeline", settingsJson: null);   // key only, no settings
        await Db.SaveChangesAsync(Ct);
        await Planner_().RecalculateAsync(task.Id, actor: null, Ct);

        var step = await StepAsync((await Service().LaunchAsync(task.Id, env.Id, ActorRef.System, ct: Ct)).Id);

        Assert.Equal("gitlab-pipeline", step.DeployStrategyKey);
        // Not the repository's gitlab-merge settings: the target supplied the key, so its (null) settings win.
        Assert.Null(step.DeploySettingsJson);
    }

    /// <summary>With no target for the environment, the repository's own strategy and settings are used.</summary>
    [Fact]
    public async Task Launch_FallsBackToTheRepositoryDefault_WhenNoTargetForTheEnvironment()
    {
        var repo = AddRepository("svc");
        repo.DeployStrategyKey = "gitlab-merge";
        repo.DeployStrategySettingsJson = """{"squash":"true"}""";
        var task = AddTask("PROJ-1");
        AddMergeRequest(repo, task);
        var env = AddEnvironment("prod");
        // A target for a DIFFERENT environment must not leak into this one.
        var other = AddEnvironment("staging", order: 1);
        AddDeployTarget(repo, other, "gitlab-pipeline");
        await Db.SaveChangesAsync(Ct);
        await Planner_().RecalculateAsync(task.Id, actor: null, Ct);

        var rollout = await Service().LaunchAsync(task.Id, env.Id, ActorRef.System, ct: Ct);

        var step = await StepAsync(rollout.Id);
        Assert.Equal("gitlab-merge", step.DeployStrategyKey);
        Assert.Equal("""{"squash":"true"}""", step.DeploySettingsJson);
    }

    /// <summary>
    /// With neither a target nor a repository default, launch refuses and names the environment, so
    /// the operator knows exactly which (repository, environment) to configure.
    /// </summary>
    [Fact]
    public async Task Launch_Fails_NamingTheEnvironment_WhenNoStrategyAnywhere()
    {
        var repo = AddRepository("svc");   // no default, no target
        var task = AddTask("PROJ-1");
        AddMergeRequest(repo, task);
        var env = AddEnvironment("prod");
        await Db.SaveChangesAsync(Ct);
        await Planner_().RecalculateAsync(task.Id, actor: null, Ct);

        var ex = await Assert.ThrowsAsync<DomainValidationException>(
            () => Service().LaunchAsync(task.Id, env.Id, ActorRef.System, ct: Ct));
        Assert.Contains("prod", ex.Message);
        Assert.Contains("deploy strategy", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ---- the readiness gate (E5) ----------------------------------------------

    /// <summary>
    /// A gated environment refuses a merge request that does not carry its label, and names it — so a
    /// production gate holds back work that has not been approved for production.
    /// </summary>
    [Fact]
    public async Task Launch_Blocks_WhenAMergeRequestIsNotReadyForTheEnvironment()
    {
        var repo = AddRepository("svc");
        repo.DeployStrategyKey = "gitlab-merge";
        var task = AddTask("PROJ-1");
        var mr = AddMergeRequest(repo, task, externalId: "7");   // status ReadyForDeploy, but no prod label
        var env = AddGatedEnvironment("prod", ReadyRule.AnyOf, "ready-for-prod");
        await Db.SaveChangesAsync(Ct);
        await Planner_().RecalculateAsync(task.Id, actor: null, Ct);

        var ex = await Assert.ThrowsAsync<DomainValidationException>(
            () => Service().LaunchAsync(task.Id, env.Id, ActorRef.System, ct: Ct));
        Assert.Contains("prod", ex.Message);
        Assert.Contains("7", ex.Message);   // the offending merge request is named
        Assert.Equal(0, await Db.Rollouts.CountAsync(Ct));   // nothing was created
    }

    /// <summary>The same launch proceeds once the merge request carries the environment's label.</summary>
    [Fact]
    public async Task Launch_Proceeds_WhenTheMergeRequestCarriesTheEnvironmentsLabel()
    {
        var repo = AddRepository("svc");
        repo.DeployStrategyKey = "gitlab-merge";
        var task = AddTask("PROJ-1");
        var mr = AddMergeRequest(repo, task);
        mr.Labels = "ready-for-prod";
        var env = AddGatedEnvironment("prod", ReadyRule.AnyOf, "ready-for-prod");
        await Db.SaveChangesAsync(Ct);
        await Planner_().RecalculateAsync(task.Id, actor: null, Ct);

        var rollout = await Service().LaunchAsync(task.Id, env.Id, ActorRef.System, ct: Ct);

        Assert.Equal("Running", rollout.Status);
    }

    /// <summary>
    /// A pin admits a merge request whose labels would fail the gate — the escape hatch for a merged
    /// merge request whose approval could not be observed from labels.
    /// </summary>
    [Fact]
    public async Task Launch_APinAdmitsAMergeRequestTheLabelsWouldRefuse()
    {
        var repo = AddRepository("svc");
        repo.DeployStrategyKey = "gitlab-merge";
        var task = AddTask("PROJ-1");
        var mr = AddMergeRequest(repo, task);   // no prod label
        var env = AddGatedEnvironment("prod", ReadyRule.AnyOf, "ready-for-prod");
        AddPin(mr.Id, env.Id, isReady: true);
        await Db.SaveChangesAsync(Ct);
        await Planner_().RecalculateAsync(task.Id, actor: null, Ct);

        var rollout = await Service().LaunchAsync(task.Id, env.Id, ActorRef.System, ct: Ct);

        Assert.Equal("Running", rollout.Status);
    }

    /// <summary>A pin can also hold: it refuses a merge request whose labels would admit it.</summary>
    [Fact]
    public async Task Launch_APinHoldsAMergeRequestTheLabelsWouldAdmit()
    {
        var repo = AddRepository("svc");
        repo.DeployStrategyKey = "gitlab-merge";
        var task = AddTask("PROJ-1");
        var mr = AddMergeRequest(repo, task);
        mr.Labels = "ready-for-prod";              // labels would pass
        var env = AddGatedEnvironment("prod", ReadyRule.AnyOf, "ready-for-prod");
        AddPin(mr.Id, env.Id, isReady: false);     // but a person holds it
        await Db.SaveChangesAsync(Ct);
        await Planner_().RecalculateAsync(task.Id, actor: null, Ct);

        await Assert.ThrowsAsync<DomainValidationException>(
            () => Service().LaunchAsync(task.Id, env.Id, ActorRef.System, ct: Ct));
    }

    /// <summary>
    /// A repository's own readiness rule for an environment applies even when the environment itself is
    /// ungated — the per-repository override the operator asked for: a stricter repository gates while
    /// the rest of the environment does not.
    /// </summary>
    [Fact]
    public async Task Launch_Blocks_WhenARepositoryOverrideRuleIsNotSatisfied_EvenIfTheEnvironmentIsUngated()
    {
        var repo = AddRepository("svc");
        repo.DeployStrategyKey = "gitlab-merge";
        var task = AddTask("PROJ-1");
        var mr = AddMergeRequest(repo, task, externalId: "7");   // no prod label
        var env = AddEnvironment("prod");                        // ungated: no environment default rule

        var rule = new ReadinessRule
        {
            Id = Guid.NewGuid(),
            Name = "prod-strict",
            Mode = ReadyRule.AnyOf,
            RequiredSignals = ReadinessSignals.Label("ready-for-prod")
        };
        Db.ReadinessRules.Add(rule);
        AddDeployTarget(repo, env, "gitlab-merge").ReadinessRuleId = rule.Id;
        await Db.SaveChangesAsync(Ct);
        await Planner_().RecalculateAsync(task.Id, actor: null, Ct);

        var ex = await Assert.ThrowsAsync<DomainValidationException>(
            () => Service().LaunchAsync(task.Id, env.Id, ActorRef.System, ct: Ct));
        Assert.Contains("7", ex.Message);
        Assert.Equal(0, await Db.Rollouts.CountAsync(Ct));
    }

    /// <summary>A rule can require a pipeline result: an MR whose pipeline is not green is held back.</summary>
    [Fact]
    public async Task Launch_Blocks_WhenARuleRequiresAGreenPipelineAndItIsNot()
    {
        var (task, env) = await SetUpPipelineGate(pipelineResult: "failed");

        await Assert.ThrowsAsync<DomainValidationException>(
            () => Service().LaunchAsync(task.Id, env.Id, ActorRef.System, ct: Ct));
    }

    /// <summary>The same launch proceeds once the pipeline is green.</summary>
    [Fact]
    public async Task Launch_Proceeds_WhenARuleRequiresAGreenPipelineAndItIs()
    {
        var (task, env) = await SetUpPipelineGate(pipelineResult: "success");

        var rollout = await Service().LaunchAsync(task.Id, env.Id, ActorRef.System, ct: Ct);

        Assert.Equal("Running", rollout.Status);
    }

    private async Task<(TaskItem Task, DeploymentEnvironment Env)> SetUpPipelineGate(string pipelineResult)
    {
        var repo = AddRepository("svc");
        repo.DeployStrategyKey = "gitlab-merge";
        var task = AddTask("PROJ-1");
        var mr = AddMergeRequest(repo, task);
        mr.PipelineResult = pipelineResult;
        var env = AddEnvironment("prod");
        var rule = new ReadinessRule
        {
            Id = Guid.NewGuid(),
            Name = "prod-pipeline",
            Mode = ReadyRule.AllOf,
            RequiredSignals = ReadinessSignals.Pipeline("success")
        };
        Db.ReadinessRules.Add(rule);
        env.ReadinessRuleId = rule.Id;
        await Db.SaveChangesAsync(Ct);
        await Planner_().RecalculateAsync(task.Id, actor: null, Ct);
        return (task, env);
    }

    // ---- redeploy (E5) --------------------------------------------------------

    private async Task<(Repository, TaskItem, MergeRequest, DeploymentEnvironment)> ReadyToRedeployAsync(RedeployPolicy policy)
    {
        var repo = AddRepository("svc");
        repo.DeployStrategyKey = "gitlab-merge";
        var task = AddTask("PROJ-1");
        var mr = AddMergeRequest(repo, task);
        var env = AddEnvironment("test");
        AddDeployTarget(repo, env, "gitlab-merge", policy: policy);
        MarkDeployed(mr.Id, env.Id);   // already deployed here
        await Db.SaveChangesAsync(Ct);
        await Planner_().RecalculateAsync(task.Id, actor: null, Ct);
        return (repo, task, mr, env);
    }

    /// <summary>
    /// With the redeploy flag AND a target that allows it, an already-deployed merge request deploys
    /// again: its step is Pending, not Skipped, and its deployment state is reset so the coordinator
    /// does not short-circuit it.
    /// </summary>
    [Fact]
    public async Task Launch_Redeploys_WhenRequestedAndTheTargetPolicyIsAlways()
    {
        var (_, task, mr, env) = await ReadyToRedeployAsync(RedeployPolicy.Always);

        var rollout = await Service().LaunchAsync(task.Id, env.Id, ActorRef.System, redeploy: true, ct: Ct);

        Assert.Equal(RolloutStepState.Pending, (await StepAsync(rollout.Id)).State);
        var state = await Db.MrDeploymentStates.AsNoTracking()
            .FirstAsync(s => s.MergeRequestId == mr.Id && s.EnvironmentId == env.Id, Ct);
        Assert.Equal(DeploymentState.NotStarted, state.State);
    }

    /// <summary>The flag alone is not enough: a target whose policy is Once still skips.</summary>
    [Fact]
    public async Task Launch_DoesNotRedeploy_WhenTheTargetPolicyIsOnce()
    {
        var (_, task, _, env) = await ReadyToRedeployAsync(RedeployPolicy.Once);

        var rollout = await Service().LaunchAsync(task.Id, env.Id, ActorRef.System, redeploy: true, ct: Ct);

        Assert.Equal(RolloutStepState.Skipped, (await StepAsync(rollout.Id)).State);
    }

    /// <summary>And the policy alone is not enough: without the flag, an Always target still skips.</summary>
    [Fact]
    public async Task Launch_DoesNotRedeploy_WithoutTheFlag()
    {
        var (_, task, _, env) = await ReadyToRedeployAsync(RedeployPolicy.Always);

        var rollout = await Service().LaunchAsync(task.Id, env.Id, ActorRef.System, redeploy: false, ct: Ct);

        Assert.Equal(RolloutStepState.Skipped, (await StepAsync(rollout.Id)).State);
    }
}
