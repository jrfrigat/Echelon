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
using ReleaseOrchestrator.Infrastructure.Persistence;
using ReleaseOrchestrator.Infrastructure.Persistence.Models;
using ReleaseOrchestrator.Infrastructure.ReleasePlanning;

namespace ReleaseOrchestrator.Infrastructure.Execution;

/// <summary>
/// Launches per-task rollouts and exposes the operator controls over a run. The step-by-step deploy
/// is driven by <see cref="RolloutCoordinator"/>; this type owns the launch gates, materialisation,
/// and the cancel / retry / skip transitions (docs/issues/007-execution-engine.md).
/// </summary>
public class RolloutService(
    AppDbContext db,
    TimeProvider clock,
    IOptions<RolloutExecutionOptions> options,
    ILogger<RolloutService> logger) : IRolloutService
{
    /// <inheritdoc/>
    public async Task<RolloutDto> LaunchAsync(Guid taskId, Guid environmentId, ActorRef actor, CancellationToken ct = default)
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

        // Idempotent: the (task, environment, plan-version) launches at most once. A live run is
        // returned unchanged, so a double-submit is a no-op. A terminal run means this plan version
        // already ran here and the key (a unique index) is taken -- recalculating the plan mints a new
        // version and a new key, which is how a re-deploy is requested. Reported as a clean domain
        // error rather than surfacing the raw unique-constraint violation as a 500.
        var idempotencyKey = $"{taskId:N}:{environmentId:N}:{plan.Id:N}";
        var existing = await db.Rollouts.AsNoTracking()
            .Where(r => r.IdempotencyKey == idempotencyKey)
            .Select(r => new { r.Id, r.Status })
            .ToListAsync(ct);
        var live = existing.FirstOrDefault(r =>
            r.Status is not (RolloutStatus.Succeeded or RolloutStatus.Failed or RolloutStatus.Cancelled));
        if (live is not null)
        {
            // Record who asked before handing back somebody else's run. This path answers 200 and
            // changes nothing, so without an event the second operator to press Launch leaves no
            // trace at all and the audit credits the run entirely to the first -- on a production
            // deploy, quite possibly the wrong person.
            db.RolloutEvents.Add(NewEvent(live.Id, RolloutEventKinds.LaunchCoalesced, actor, now,
                new { environment = env.Key, existingStatus = live.Status.ToString() }));
            await db.SaveChangesAsync(ct);

            return (await GetAsync(live.Id, ct))!;
        }
        if (existing.Count > 0)
            throw new DomainValidationException(
                $"This plan version has already been rolled out to '{env.Key}'. Recalculate the plan to roll out again.");

        var closure = plan.Nodes.Select(n => n.TaskId).ToHashSet();
        var mrIds = plan.Nodes.SelectMany(n => n.Items.Select(i => i.MergeRequestId)).Distinct().ToList();

        // Readiness: every prerequisite task in the closure must be deployed in this environment.
        var blocking = await BlockingTasksAsync(closure, exceptTaskId: taskId, environmentId, ct);
        if (blocking.Count > 0)
            throw new DomainValidationException(
                $"Task is not ready in '{env.Key}': waiting on {string.Join(", ", blocking)}.");

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

        // Waves come from the same pure graph the plan is shown with, over the plan's merge requests.
        var planInputs = await db.MergeRequests
            .Where(m => mrIds.Contains(m.Id))
            .OrderBy(m => m.CreatedAt).ThenBy(m => m.Id)
            .Select(PlanInput.FromEntity)
            .AsSplitQuery().AsNoTracking()
            .ToListAsync(ct);
        var graph = ReleasePlanGraph.Build(planInputs);
        var waveOf = new Dictionary<Guid, int>();
        for (int i = 0; i < graph.Stages.Count; i++)
            foreach (var id in graph.Stages[i]) waveOf[id] = i + 1;

        var mrDetails = await db.MergeRequests
            .Where(m => mrIds.Contains(m.Id))
            .Select(m => new
            {
                m.Id, m.ExternalId, m.TaskId, m.RepositoryId,
                RepoName = m.Repository.Name,
                RepoDeployKey = m.Repository.DeployStrategyKey
            })
            .ToListAsync(ct);

        var overrideOf = plan.Nodes.SelectMany(n => n.Items)
            .ToDictionary(i => i.MergeRequestId, i => i.DeployStrategyKeyOverride);

        var alreadyDeployed = (await db.MrDeploymentStates
                .Where(s => s.EnvironmentId == environmentId && mrIds.Contains(s.MergeRequestId)
                            && (s.State == DeploymentState.Deployed || s.State == DeploymentState.Skipped))
                .Select(s => s.MergeRequestId)
                .ToListAsync(ct))
            .ToHashSet();

        var steps = new List<RolloutStep>();
        var snapshot = new List<StepSnapshot>();
        foreach (var mr in mrDetails)
        {
            var deployKey = overrideOf.GetValueOrDefault(mr.Id) ?? mr.RepoDeployKey;
            if (string.IsNullOrWhiteSpace(deployKey))
                throw new DomainValidationException(
                    $"Repository '{mr.RepoName}' has no deploy strategy; set one before launching.");

            var wave = waveOf.GetValueOrDefault(mr.Id, 1);
            var skipped = alreadyDeployed.Contains(mr.Id);
            steps.Add(new RolloutStep
            {
                Id = Guid.NewGuid(),
                MergeRequestId = mr.Id,
                TaskId = mr.TaskId!.Value,
                Wave = wave,
                DeployStrategyKey = deployKey,
                State = skipped ? RolloutStepState.Skipped : RolloutStepState.Pending,
                AttemptCount = 0,
                StartedAt = skipped ? now : null,
                FinishedAt = skipped ? now : null
            });
            snapshot.Add(new StepSnapshot(mr.Id, mr.TaskId!.Value, wave, deployKey));
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
            // A concurrent launch inserted the same (task, environment, plan) between the check above
            // and here -- the unique index on IdempotencyKey caught it. Report it cleanly rather than
            // as a 500; rethrow anything that is not that conflict.
            if (await db.Rollouts.AsNoTracking().AnyAsync(r => r.IdempotencyKey == idempotencyKey && r.Id != rollout.Id, ct))
                throw new DomainValidationException($"This plan version is already being rolled out to '{env.Key}'.");
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

    /// <summary>The rollout's frozen plan, one entry per merge request.</summary>
    private sealed record StepSnapshot(Guid MergeRequestId, Guid TaskId, int Wave, string DeployStrategyKey);
}
