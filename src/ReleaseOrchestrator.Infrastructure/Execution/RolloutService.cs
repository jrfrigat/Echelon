using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ReleaseOrchestrator.Application.Auditing;
using ReleaseOrchestrator.Application.DTOs;
using ReleaseOrchestrator.Application.Exceptions;
using ReleaseOrchestrator.Application.ReleasePlanning;
using ReleaseOrchestrator.Application.Services;
using ReleaseOrchestrator.Core.Enums;
using ReleaseOrchestrator.Core.Parsing;
using ReleaseOrchestrator.Infrastructure.Persistence;
using ReleaseOrchestrator.Infrastructure.Persistence.Models;
using ReleaseOrchestrator.Infrastructure.ReleasePlanning;

namespace ReleaseOrchestrator.Infrastructure.Execution;

/// <summary>
/// Launches per-task rollouts and exposes the operator controls over a run. The step-by-step deploy
/// is driven by <see cref="RolloutCoordinator"/>; this type owns the launch gates, materialisation,
/// and the cancel / retry / skip transitions.
/// </summary>
public class RolloutService(
    AppDbContext db,
    TimeProvider clock,
    IOptions<RolloutExecutionOptions> options,
    ILogger<RolloutService> logger) : IRolloutService
{
    /// <inheritdoc/>
    public async Task<RolloutDto> LaunchAsync(Guid taskId, Guid environmentId, ActorRef actor, bool redeploy = false, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow().UtcDateTime;

        var task = await db.Tasks.FirstOrDefaultAsync(t => t.Id == taskId, ct)
            ?? throw new NotFoundException($"Task {taskId} not found");
        var env = await db.DeploymentEnvironments.FirstOrDefaultAsync(e => e.Id == environmentId, ct)
            ?? throw new NotFoundException($"Environment {environmentId} not found");
        if (!env.IsEnabled)
            throw new DomainValidationException($"Environment '{env.Key}' is disabled.");

        var plan = await db.RolloutPlans
            .Include(p => p.Nodes).ThenInclude(n => n.Items)
            .FirstOrDefaultAsync(p => p.TargetTaskId == taskId && p.IsActive, ct)
            ?? throw new DomainValidationException("This task has no active plan. Recalculate the plan before launching.");

        // A plan may be edited into violating a mandatory edge (never silently -- ConflictsJson), but
        // the executor must not deploy a known-bad order (docs/issues/006-per-task-planning.md).
        if (!string.IsNullOrEmpty(plan.ConflictsJson))
            throw new DomainValidationException("The plan violates a mandatory dependency and cannot be launched. Fix the plan first.");

        // Idempotent, and single-flight per (task, environment). The key embeds plan.Id, which a
        // recalculation rotates on every ingestion event, so a liveness check on the key alone would
        // let a fresh plan version launch a *second* concurrent run into the same environment while
        // the first is still going -- the exact hole this closes. The deploy claims would still stop
        // a merge request deploying twice, but two runs driving the same steps is wrong on its own,
        // so liveness keys on the pair, not the key.
        var idempotencyKey = $"{taskId:N}:{environmentId:N}:{plan.Id:N}";
        var forPair = await db.Rollouts.AsNoTracking()
            .Where(r => r.TargetTaskId == taskId && r.EnvironmentId == environmentId)
            .Select(r => new { r.Id, r.Status, r.IdempotencyKey })
            .ToListAsync(ct);

        // Prefer the exact-key live row when one exists, so a double-submit of the current plan
        // version always coalesces rather than depending on which unordered row the query returned
        // first. (Two live rows for one pair should not exist -- the database enforces that below --
        // but if they ever did, this makes the decision deterministic instead of row-order dependent.)
        bool IsLive(RolloutStatus s) => s is not (RolloutStatus.Succeeded or RolloutStatus.Failed or RolloutStatus.Cancelled);
        var live =
            forPair.FirstOrDefault(r => r.IdempotencyKey == idempotencyKey && IsLive(r.Status))
            ?? forPair.FirstOrDefault(r => IsLive(r.Status));
        if (live is not null)
        {
            // Same plan version: a genuine double-submit, coalesced to a no-op that hands back the
            // running run. Record who asked first -- this path answers 200 and changes nothing, so
            // without an event the second operator to press Launch leaves no trace and the audit
            // credits the run entirely to the first, on a production deploy quite possibly the wrong
            // person.
            if (live.IdempotencyKey == idempotencyKey)
            {
                db.RolloutEvents.Add(NewEvent(live.Id, RolloutEventKinds.LaunchCoalesced, actor, now,
                    new { environment = env.Key, existingStatus = live.Status.ToString() }));
                await db.SaveChangesAsync(ct);

                return (await GetAsync(live.Id, ct))!;
            }

            // A different plan version is already running here. Not a double-submit to coalesce and
            // not a second run to start -- refuse, and say how to proceed.
            throw new DomainValidationException(
                $"A rollout of this task to '{env.Key}' is already running. "
                + "Wait for it to finish or cancel it before launching again.");
        }

        // No live run for the pair. If this exact plan version already ran here and finished, it is
        // done -- recalculating the plan mints a new version, which is how a re-deploy is requested.
        // (A finished run under a *different* version falls through: that is the redeploy path.)
        if (forPair.Any(r => r.IdempotencyKey == idempotencyKey))
            throw new DomainValidationException(
                $"This plan version has already been rolled out to '{env.Key}'. Recalculate the plan to roll out again.");

        var closure = plan.Nodes.Select(n => n.TaskId).ToHashSet();
        var mrIds = plan.Nodes.SelectMany(n => n.Items.Select(i => i.MergeRequestId)).Distinct().ToList();

        // A plan with nothing in it produces a run with no steps, which nothing can ever advance: it
        // stays Running for good, and the liveness check below then refuses every later launch for
        // this pair with "a rollout is already running". Refusing up front is the difference between
        // an answer and a wedged pair that only a database edit clears.
        if (mrIds.Count == 0)
            throw new DomainValidationException(
                "This plan has no merge requests to deploy. The task may have none yet, or they may all "
                + "be excluded from the rollout.");

        // Readiness: every prerequisite task in the closure must be deployed in this environment.
        var blocking = await BlockingTasksAsync(closure, exceptTaskId: taskId, environmentId, ct);
        if (blocking.Count > 0)
            throw new DomainValidationException(
                $"Task is not ready in '{env.Key}': waiting on {string.Join(", ", blocking)}.");

        // Work that has started and not landed: a branch naming a task in this plan, with no merge
        // request to carry it.
        await GuardUnlandedBranchesAsync(closure, ct);

        // Environment-progression gate: the whole closure must already be deployed to every enabled
        // environment ordered before this one.
        if (options.Value.EnvironmentProgressionGate)
        {
            var earlier = await db.DeploymentEnvironments
                .Where(e => e.IsEnabled && e.Order < env.Order)
                .OrderBy(e => e.Order)
                .Select(e => new { e.Id, e.Key })
                .ToListAsync(ct);

            foreach (var e in earlier)
            {
                var notThere = await BlockingTasksAsync(closure, exceptTaskId: null, e.Id, ct);
                if (notThere.Count > 0)
                    throw new DomainValidationException(
                        $"Must deploy to '{e.Key}' before '{env.Key}' (progression gate): {string.Join(", ", notThere)} not deployed there.");
            }
        }

        // Waves come from the plan being launched, not from a fresh derivation.
        //
        // This used to call ReleasePlanGraph.Build over the plan's merge requests, reading their EF
        // navigations directly -- which meant the deploy order ignored everything the planner had been
        // told: the wait policy, the operator's edge overrides and the ordering-rule document all
        // vanished at exactly the moment they mattered, and the rollout ran an order nobody had
        // approved. One derivation, in the planner, recorded on the plan (006 §1).
        var waveOf = plan.Nodes
            .SelectMany(n => n.Items)
            .GroupBy(i => i.MergeRequestId)
            .ToDictionary(g => g.Key, g => g.Max(i => i.Wave));

        // A plan stored before waves were recorded has none. Refusing is the only safe answer: any
        // fallback here would be the second derivation this change exists to remove, and every
        // ingestion event rebuilds active plans, so the fix is immediate.
        if (waveOf.Values.Any(w => w <= 0))
            throw new DomainValidationException(
                "This plan version predates recorded deploy waves. Recalculate the plan before launching.");

        var mrDetails = await db.MergeRequests
            .Where(m => mrIds.Contains(m.Id))
            .Select(m => new
            {
                m.Id, m.ExternalId, m.TaskId, m.RepositoryId, m.Labels, m.Status, m.PipelineResult,
                RepoName = m.Repository.Name,
                RepoDeployKey = m.Repository.DeployStrategyKey,
                RepoDeploySettings = m.Repository.DeployStrategySettingsJson
            })
            .ToListAsync(ct);

        // The per-environment deploy configuration, if any, for the repositories in this rollout.
        // A target overrides the repository's default strategy for this one environment, which is
        // how the same repository merges on prod but triggers a pipeline on a test rig.
        var repoIds = mrDetails.Select(m => m.RepositoryId).Distinct().ToList();
        var targetOf = (await db.RepositoryDeployTargets
                .Where(t => t.EnvironmentId == environmentId && repoIds.Contains(t.RepositoryId))
                .Select(t => new { t.RepositoryId, t.DeployStrategyKey, t.DeploySettingsJson, t.RedeployPolicy, t.ReadinessRuleId })
                .ToListAsync(ct))
            .ToDictionary(t => t.RepositoryId);

        var overrideOf = plan.Nodes.SelectMany(n => n.Items)
            .ToDictionary(i => i.MergeRequestId, i => i.DeployStrategyKeyOverride);

        var alreadyDeployed = (await db.MrDeploymentStates
                .Where(s => s.EnvironmentId == environmentId && mrIds.Contains(s.MergeRequestId)
                            && (s.State == DeploymentState.Deployed || s.State == DeploymentState.Skipped))
                .Select(s => s.MergeRequestId)
                .ToListAsync(ct))
            .ToHashSet();

        // Redeploy: an already-deployed merge request is deployed again only when the launch asks for
        // it AND its repository's target for this environment permits it (RedeployPolicy.Always). Two
        // independent conditions -- production defaults to Once, so a stray redeploy flag cannot touch
        // it, and a standing Always policy cannot redeploy without the flag. Absent a target the
        // policy is unknown, so it is not redeployable.
        var redeploying = !redeploy
            ? new HashSet<Guid>()
            : mrDetails
                .Where(m => alreadyDeployed.Contains(m.Id)
                            && targetOf.GetValueOrDefault(m.RepositoryId)?.RedeployPolicy == RedeployPolicy.Always)
                .Select(m => m.Id)
                .ToHashSet();

        // Readiness gate: every merge request that would actually deploy must be ready for this
        // environment. Blocks the whole launch rather than filtering the unready ones out silently --
        // a task's merge requests have ordering dependencies, so deploying some and quietly dropping
        // others is a partial rollout that reads as complete. The operator makes them ready (a label
        // or a pin) or picks an ungated environment. Already-deployed merge requests are exempt unless
        // they are being redeployed -- a redeploy is a fresh deploy and must clear the gate again.
        var deploying = mrDetails
            .Where(m => !alreadyDeployed.Contains(m.Id) || redeploying.Contains(m.Id))
            .Select(m => (m.Id, m.ExternalId, m.Labels, m.Status, m.RepositoryId, m.PipelineResult))
            .ToList();
        // The readiness rule per merge request: its repository's override for this environment, else
        // the environment's default. Null on either means "no gate" for that repository.
        var ruleOverrideOf = mrDetails
            .Select(m => m.RepositoryId).Distinct()
            .ToDictionary(id => id, id => targetOf.GetValueOrDefault(id)?.ReadinessRuleId);
        await GuardReadinessAsync(env, ruleOverrideOf, deploying, ct);

        var steps = new List<RolloutStep>();
        var snapshot = new List<StepSnapshot>();
        foreach (var mr in mrDetails)
        {
            // Resolve the key and its settings from the SAME source: per-item override, then this
            // environment's target, then the repository's default. Decoupling them is a real hazard --
            // the strategy would receive another strategy's settings, and its secret values as
            // ciphertext, because dispatch decrypts using the schema of the key it actually runs. So a
            // target that supplies the key supplies its settings too, even when those are null (that
            // strategy needs none); only a repository-default key uses the repository's settings.
            // Frozen onto the step below so a later config edit cannot change how this run deploys.
            var target = targetOf.GetValueOrDefault(mr.RepositoryId);
            var overrideKey = overrideOf.GetValueOrDefault(mr.Id);

            string? deployKey;
            string? deploySettings;
            if (overrideKey is { Length: > 0 })
            {
                // A per-item override names only a key; it carries no settings of its own, and no
                // current path writes one. A future override feature must decide its settings source
                // rather than inherit a mismatched one.
                deployKey = overrideKey;
                deploySettings = null;
            }
            else if (target is not null)
            {
                deployKey = target.DeployStrategyKey;
                deploySettings = target.DeploySettingsJson;
            }
            else
            {
                deployKey = mr.RepoDeployKey;
                deploySettings = mr.RepoDeploySettings;
            }

            if (string.IsNullOrWhiteSpace(deployKey))
                throw new DomainValidationException(
                    $"Repository '{mr.RepoName}' has no deploy strategy for environment '{env.Key}'; "
                    + "configure a deploy target for it, or a repository default, before launching.");

            // Every merge request in a plan had a task when the plan was built -- the planner selects on
            // exactly that -- but MergeRequest.TaskId is SetNull, so archiving the task blanks the link
            // and a plan built beforehand still names this row. Both step columns are non-nullable, so
            // without this the launch is a NullReferenceException and a 500 that says nothing. Refuse
            // with the one instruction that fixes it.
            if (mr.TaskId is not { } stepTaskId)
                throw new DomainValidationException(
                    $"Merge request '{mr.ExternalId}' ({mr.RepoName}) is no longer linked to a task, so the "
                    + "plan is stale. Recalculate the plan before launching.");

            var wave = waveOf.GetValueOrDefault(mr.Id, 1);
            // Already-deployed skips, unless it is being redeployed.
            var skipped = alreadyDeployed.Contains(mr.Id) && !redeploying.Contains(mr.Id);
            steps.Add(new RolloutStep
            {
                Id = Guid.NewGuid(),
                MergeRequestId = mr.Id,
                TaskId = stepTaskId,
                Wave = wave,
                DeployStrategyKey = deployKey,
                DeploySettingsJson = deploySettings,
                State = skipped ? RolloutStepState.Skipped : RolloutStepState.Pending,
                AttemptCount = 0,
                StartedAt = skipped ? now : null,
                FinishedAt = skipped ? now : null
            });
            snapshot.Add(new StepSnapshot(mr.Id, stepTaskId, wave, deployKey));
        }

        // Clear the deployment state of the merge requests being redeployed, in the same unit of work
        // as the rollout below, so it commits atomically. The coordinator treats a Deployed/Skipped
        // state as "already done" and short-circuits; resetting to NotStarted lets it re-claim (the
        // claim was Released when the earlier deploy settled) and deploy again.
        if (redeploying.Count > 0)
        {
            var states = await db.MrDeploymentStates
                .Where(s => s.EnvironmentId == environmentId && redeploying.Contains(s.MergeRequestId))
                .ToListAsync(ct);
            foreach (var s in states)
            {
                s.State = DeploymentState.NotStarted;
                s.UpdatedAt = now;
            }
        }

        var rollout = new Rollout
        {
            Id = Guid.NewGuid(),
            TargetTaskId = taskId,
            RolloutPlanId = plan.Id,
            EnvironmentId = environmentId,
            PlanSnapshotJson = JsonSerializer.Serialize(snapshot),
            Status = RolloutStatus.Running,
            LaunchedByOid = actor.Oid,
            LaunchedByKind = actor.Kind,
            LaunchedByName = actor.DisplayName,
            IdempotencyKey = idempotencyKey,
            StartedAt = now,
            Steps = steps
        };

        db.Rollouts.Add(rollout);
        // Same actor on both rows, written in one unit of work, so the run and its opening event
        // cannot disagree about who launched it.
        db.RolloutEvents.Add(NewEvent(rollout.Id, RolloutEventKinds.Launched, actor, now,
            new { environment = env.Key, steps = steps.Count }));

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // A concurrent launch slipped between the liveness check above and here. Two unique
            // indexes can catch it: IdempotencyKey (the same plan version, double-submitted), or the
            // filtered one that allows only a single live rollout per (task, environment) -- the case
            // of two launches straddling a plan recalculation, whose rotating keys differ so the key
            // index would not fire and the read-based check could not see. Re-query to report which,
            // cleanly, rather than surfacing the raw constraint violation as a 500.
            var others = await db.Rollouts.AsNoTracking()
                .Where(r => r.Id != rollout.Id && r.TargetTaskId == taskId && r.EnvironmentId == environmentId)
                .Select(r => new { r.Status, r.IdempotencyKey })
                .ToListAsync(ct);

            if (others.Any(r => r.IdempotencyKey == idempotencyKey))
                throw new DomainValidationException($"This plan version is already being rolled out to '{env.Key}'.");
            if (others.Any(r => IsLive(r.Status)))
                throw new DomainValidationException(
                    $"A rollout of this task to '{env.Key}' is already running. "
                    + "Wait for it to finish or cancel it before launching again.");
            throw;
        }

        logger.LogInformation(
            "Rollout {Rollout} launched for task {Task} into {Env}: {Steps} step(s).",
            rollout.Id, task.ExternalId, env.Key, steps.Count);

        return (await GetAsync(rollout.Id, ct))!;
    }

    /// <inheritdoc/>
    public async Task<RolloutDto?> GetAsync(Guid rolloutId, CancellationToken ct = default)
    {
        var r = await db.Rollouts
            .Include(x => x.TargetTask)
            .Include(x => x.Environment)
            .Include(x => x.Steps).ThenInclude(s => s.MergeRequest).ThenInclude(m => m.Repository)
            .Include(x => x.Steps).ThenInclude(s => s.Task)
            .AsSplitQuery().AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == rolloutId, ct);
        if (r is null) return null;

        var steps = r.Steps
            .OrderBy(s => s.Wave).ThenBy(s => s.MergeRequest.Repository.Name).ThenBy(s => s.MergeRequest.ExternalId)
            .Select(s => new RolloutStepDto(
                s.Id, s.MergeRequestId, s.MergeRequest.ExternalId, s.MergeRequest.Repository.Name,
                s.TaskId, s.Task.ExternalId, s.Wave, s.State.ToString(), s.AttemptCount, s.ExternalRef, s.LastError))
            .ToList();

        return new RolloutDto(
            r.Id, r.TargetTaskId, r.TargetTask.ExternalId, r.EnvironmentId, r.Environment.Key,
            r.Status.ToString(), r.StartedAt, r.FinishedAt, steps);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<RolloutSummaryDto>> ListAsync(Guid? taskId, CancellationToken ct = default)
    {
        var query = db.Rollouts.AsNoTracking();
        if (taskId is not null) query = query.Where(r => r.TargetTaskId == taskId);

        return await query
            .OrderByDescending(r => r.StartedAt)
            .Take(200)
            .Select(r => new RolloutSummaryDto(
                r.Id, r.TargetTaskId, r.TargetTask.ExternalId, r.Environment.Key,
                r.Status.ToString(), r.StartedAt, r.FinishedAt,
                r.Steps.Count,
                r.Steps.Count(s => s.State == RolloutStepState.Succeeded || s.State == RolloutStepState.Skipped)))
            .ToListAsync(ct);
    }

    /// <inheritdoc/>
    public async Task CancelAsync(Guid rolloutId, ActorRef actor, CancellationToken ct = default)
    {
        var r = await db.Rollouts.Include(x => x.Steps).FirstOrDefaultAsync(x => x.Id == rolloutId, ct)
            ?? throw new NotFoundException($"Rollout {rolloutId} not found");

        // Cancelling belongs in this guard: a second cancel of an already-cancelling run assigns the
        // status it already has, EF emits no UPDATE, and the call answers 204 having done nothing.
        // Without it the audit would gain an event for a change that never happened.
        if (r.Status is RolloutStatus.Succeeded or RolloutStatus.Failed or RolloutStatus.Cancelled or RolloutStatus.Cancelling)
            throw new DomainValidationException($"Rollout is already {r.Status} and cannot be cancelled.");

        var now = clock.GetUtcNow().UtcDateTime;

        // If nothing is in flight, cancel outright; otherwise let in-flight steps settle.
        var inFlight = r.Steps.Any(s => s.State is RolloutStepState.Deploying or RolloutStepState.Awaiting or RolloutStepState.Claimed);
        r.Status = inFlight ? RolloutStatus.Cancelling : RolloutStatus.Cancelled;
        if (!inFlight) r.FinishedAt = now;

        // Two facts, two kinds. When steps are still in flight this only REQUESTS cancellation and
        // the coordinator writes Cancelled later when the run actually settles; emitting Cancelled
        // here as well put two identical entries on the timeline for one operator action, the second
        // of them attributed to the coordinator.
        db.RolloutEvents.Add(NewEvent(
            r.Id,
            inFlight ? RolloutEventKinds.CancelRequested : RolloutEventKinds.Cancelled,
            actor, now,
            new { settling = inFlight }));
        await db.SaveChangesAsync(ct);
    }

    /// <inheritdoc/>
    public async Task RetryStepAsync(Guid rolloutId, Guid stepId, ActorRef actor, CancellationToken ct = default)
    {
        var (r, step) = await LoadStepAsync(rolloutId, stepId, ct);
        if (step.State != RolloutStepState.Failed)
            throw new DomainValidationException("Only a failed step can be retried.");

        var now = clock.GetUtcNow().UtcDateTime;

        // The error text is captured into the event before it is cleared: after this method the step
        // reads Pending with no error, so nothing downstream could reconstruct what was retried or why.
        db.RolloutEvents.Add(NewEvent(r.Id, RolloutEventKinds.StepRetried, actor, now,
            new { stepId = step.Id, mergeRequestId = step.MergeRequestId, previousError = step.LastError }));

        step.State = RolloutStepState.Pending;
        step.LastError = null;
        if (r.Status == RolloutStatus.Paused) r.Status = RolloutStatus.Running;
        await db.SaveChangesAsync(ct);
    }

    /// <inheritdoc/>
    public async Task SkipStepAsync(Guid rolloutId, Guid stepId, ActorRef actor, CancellationToken ct = default)
    {
        var (r, step) = await LoadStepAsync(rolloutId, stepId, ct);
        if (step.State is RolloutStepState.Succeeded or RolloutStepState.Skipped)
            throw new DomainValidationException($"Step is already {step.State}.");

        var now = clock.GetUtcNow().UtcDateTime;

        // Skipping declares the merge request deployed in this environment without deploying it, and
        // the readiness gate trusts that for every later rollout. Of everything an operator can do
        // here it is the action most worth being able to attribute afterwards.
        db.RolloutEvents.Add(NewEvent(r.Id, RolloutEventKinds.StepSkipped, actor, now,
            new { stepId = step.Id, mergeRequestId = step.MergeRequestId, previousState = step.State.ToString() }));

        step.State = RolloutStepState.Skipped;
        step.FinishedAt = now;
        await UpsertDeploymentStateAsync(step.MergeRequestId, r.EnvironmentId, DeploymentState.Skipped, now, ct);
        if (r.Status == RolloutStatus.Paused) r.Status = RolloutStatus.Running;
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Builds an event row. Added to the context by the caller so it lands in the caller's unit of
    /// work — an audit row that could commit while the change it describes rolled back would be
    /// worse than no audit row.
    /// </summary>
    private static RolloutEvent NewEvent(Guid rolloutId, string kind, ActorRef actor, DateTime at, object? payload) =>
        new()
        {
            Id = Guid.NewGuid(),
            RolloutId = rolloutId,
            Kind = kind,
            ActorOid = actor.Oid,
            ActorKind = actor.Kind,
            ActorName = actor.DisplayName,
            PayloadJson = payload is null ? null : JsonSerializer.Serialize(payload),
            At = at
        };

    private async Task<(Rollout, RolloutStep)> LoadStepAsync(Guid rolloutId, Guid stepId, CancellationToken ct)
    {
        var r = await db.Rollouts.Include(x => x.Steps).FirstOrDefaultAsync(x => x.Id == rolloutId, ct)
            ?? throw new NotFoundException($"Rollout {rolloutId} not found");
        var step = r.Steps.FirstOrDefault(s => s.Id == stepId)
            ?? throw new NotFoundException($"Step {stepId} not found in rollout {rolloutId}");
        return (r, step);
    }

    private async Task UpsertDeploymentStateAsync(Guid mergeRequestId, Guid environmentId, DeploymentState state, DateTime now, CancellationToken ct)
    {
        var existing = await db.MrDeploymentStates
            .FirstOrDefaultAsync(s => s.MergeRequestId == mergeRequestId && s.EnvironmentId == environmentId, ct);
        if (existing is null)
            db.MrDeploymentStates.Add(new MrDeploymentState
            {
                MergeRequestId = mergeRequestId, EnvironmentId = environmentId, State = state, UpdatedAt = now
            });
        else
        {
            existing.State = state;
            existing.UpdatedAt = now;
        }
    }

    /// <summary>
    /// The keys of tasks in the closure (optionally excluding one) that are not fully deployed in an
    /// environment. A task with no deployable merge requests is auto-satisfied.
    /// </summary>
    private async Task<List<string>> BlockingTasksAsync(
        HashSet<Guid> closureTaskIds, Guid? exceptTaskId, Guid environmentId, CancellationToken ct)
    {
        var taskIds = closureTaskIds.Where(id => id != exceptTaskId).ToList();
        if (taskIds.Count == 0) return [];

        var mrs = await db.MergeRequests
            .Where(m => m.TaskId != null && taskIds.Contains(m.TaskId.Value) && m.Status != MergeRequestStatus.Closed)
            .Select(m => new { m.Id, TaskKey = m.Task!.ExternalId })
            .ToListAsync(ct);
        if (mrs.Count == 0) return [];

        var mrIds = mrs.Select(m => m.Id).ToList();
        var deployed = (await db.MrDeploymentStates
                .Where(s => s.EnvironmentId == environmentId && mrIds.Contains(s.MergeRequestId)
                            && (s.State == DeploymentState.Deployed || s.State == DeploymentState.Skipped))
                .Select(s => s.MergeRequestId)
                .ToListAsync(ct))
            .ToHashSet();

        return mrs.Where(m => !deployed.Contains(m.Id))
            .Select(m => m.TaskKey).Distinct().OrderBy(k => k, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// Refuses the launch when a task in the plan still has an unlanded branch that no merge request
    /// carries.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A plan is built from merge requests, so a task whose only artefact is a branch used to look
    /// finished — nothing to deploy, nothing to wait for. That is backwards: a branch is work that
    /// started, and rolling out a parent while a child's branch is unlanded ships an incomplete change.
    /// </para>
    /// <para>
    /// The rule is deliberately "no merge request carries it", not "any unmerged branch". Every merge
    /// request in a plan has an unmerged source branch at launch — that is what the rollout is about to
    /// merge — so blocking on those would block every launch. What blocks is a branch nobody has raised
    /// for review: the work exists, and the plan does not know about it.
    /// </para>
    /// </remarks>
    private async Task GuardUnlandedBranchesAsync(IReadOnlyCollection<Guid> closure, CancellationToken ct)
    {
        if (closure.Count == 0) return;

        // The keys the branches are linked by. A branch stores the task's external id, not its row id:
        // it is often pushed before the task is imported.
        var taskKeys = await db.Tasks
            .Where(t => closure.Contains(t.Id))
            .Select(t => t.ExternalId)
            .ToListAsync(ct);
        if (taskKeys.Count == 0) return;

        var unlanded = await db.RepositoryBranches
            .Where(b => !b.IsMerged
                        && !b.IsDefault
                        && b.TaskExternalId != null
                        && taskKeys.Contains(b.TaskExternalId))
            .Select(b => new { b.Name, b.RepositoryId, b.TaskExternalId, RepositoryName = b.Repository.Name })
            .ToListAsync(ct);
        if (unlanded.Count == 0) return;

        // A branch that a merge request already carries is represented in the plan by that merge
        // request, so it is not unplanned work.
        var repoIds = unlanded.Select(b => b.RepositoryId).Distinct().ToList();
        var carried = (await db.MergeRequests
                .Where(m => repoIds.Contains(m.RepositoryId))
                .Select(m => new { m.RepositoryId, m.SourceBranch })
                .ToListAsync(ct))
            .Select(m => (m.RepositoryId, m.SourceBranch))
            .ToHashSet();

        var offenders = unlanded
            .Where(b => !carried.Contains((b.RepositoryId, b.Name)))
            .Select(b => $"{b.TaskExternalId} ({b.RepositoryName}: {b.Name})")
            .Distinct(StringComparer.Ordinal)
            .OrderBy(s => s, StringComparer.Ordinal)
            .ToList();
        if (offenders.Count == 0) return;

        throw new DomainValidationException(
            "Unfinished work blocks this rollout — these branches have no merge request and are not merged: "
            + string.Join(", ", offenders)
            + ". Raise a merge request for each, or merge or delete the branch.");
    }

    /// <summary>
    /// Refuses the launch if any merge request that would deploy is not ready for the environment.
    /// </summary>
    /// <param name="env">The target environment, carrying its default readiness rule (or none).</param>
    /// <param name="ruleOverrideByRepo">
    /// Each repository's readiness-rule override for this environment (its deploy target's rule), or
    /// null where the repository has no override and falls back to the environment default.
    /// </param>
    /// <param name="deploying">The merge requests that would deploy (already-deployed ones excluded).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <remarks>
    /// The one place a rollout consults the readiness feature, evaluated once here at launch. Each
    /// merge request's rule is resolved as pin &gt; repository override &gt; environment default &gt; no
    /// gate, then its current signals — a token per label, one for its status, one for its pipeline
    /// result when known (<see cref="ReadinessSignals"/>) — are checked against the rule. A merge
    /// request whose rule resolves to none is ungated and pays nothing, exactly as before there was a
    /// gate. There is no dispatch-time re-check: a pin or signal changed after launch does not affect a
    /// run already materialised, so an operator who needs to hold a launched rollout cancels it.
    /// </remarks>
    private async Task GuardReadinessAsync(
        DeploymentEnvironment env,
        IReadOnlyDictionary<Guid, Guid?> ruleOverrideByRepo,
        IReadOnlyList<(Guid Id, string ExternalId, string Labels, MergeRequestStatus Status, Guid RepositoryId, string? PipelineResult)> deploying,
        CancellationToken ct)
    {
        if (deploying.Count == 0) return;

        Guid? RuleIdFor(Guid repositoryId) =>
            ruleOverrideByRepo.GetValueOrDefault(repositoryId) ?? env.ReadinessRuleId;

        // Nothing to load or check when every deploying merge request resolves to no gate.
        var ruleIds = deploying.Select(m => RuleIdFor(m.RepositoryId)).OfType<Guid>().Distinct().ToList();
        if (ruleIds.Count == 0) return;

        var rules = (await db.ReadinessRules.AsNoTracking()
                .Where(r => ruleIds.Contains(r.Id))
                .Select(r => new { r.Id, r.Mode, r.RequiredSignals })
                .ToListAsync(ct))
            .ToDictionary(r => r.Id);

        var mrIds = deploying.Select(m => m.Id).ToList();
        var pinOf = (await db.MergeRequestReadinessPins
                .Where(p => p.EnvironmentId == env.Id && mrIds.Contains(p.MergeRequestId))
                .Select(p => new { p.MergeRequestId, p.IsReady })
                .ToListAsync(ct))
            .ToDictionary(p => p.MergeRequestId, p => (bool?)p.IsReady);

        bool IsReady((Guid Id, string ExternalId, string Labels, MergeRequestStatus Status, Guid RepositoryId, string? PipelineResult) mr)
        {
            var pin = pinOf.GetValueOrDefault(mr.Id);

            // No rule resolved: the merge request is ungated, ready unless a pin deliberately holds it.
            if (RuleIdFor(mr.RepositoryId) is not { } id || !rules.TryGetValue(id, out var rule))
                return pin ?? true;

            // The merge request's current signals: a token per label, one for its status, one for its
            // pipeline result when known. The rule's required set is canonical already, so splitting
            // it is enough; RemoveEmptyEntries so an empty set is [] and the resolver's non-empty guard
            // refuses it rather than admitting everything.
            var signals = ReadinessSignals.For(
                mr.Labels.Split(',', StringSplitOptions.RemoveEmptyEntries), mr.Status, mr.PipelineResult);
            var required = rule.RequiredSignals.Split(',', StringSplitOptions.RemoveEmptyEntries);
            return ReadinessEvaluator.Evaluate(signals, required, rule.Mode, pin).IsReady;
        }

        var notReady = deploying
            .Where(m => !IsReady(m))
            .Select(m => m.ExternalId)
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();

        if (notReady.Count > 0)
            throw new DomainValidationException(
                $"Not ready for '{env.Key}': merge request(s) {string.Join(", ", notReady)}. "
                + "Give them the signals their rule requires (a label, a merge, a green pipeline) or pin them ready, "
                + "or launch to an ungated environment.");
    }

    /// <summary>The rollout's frozen plan, one entry per merge request.</summary>
    private sealed record StepSnapshot(Guid MergeRequestId, Guid TaskId, int Wave, string DeployStrategyKey);
}
