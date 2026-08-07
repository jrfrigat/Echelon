using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ReleaseOrchestrator.Application.Auditing;
using ReleaseOrchestrator.Application.DTOs;
using ReleaseOrchestrator.Application.Services;
using ReleaseOrchestrator.Core.Enums;
using ReleaseOrchestrator.Infrastructure.Actions;
using ReleaseOrchestrator.Infrastructure.Auth;
using ReleaseOrchestrator.Infrastructure.Persistence;
using ReleaseOrchestrator.Infrastructure.Persistence.Models;
using ReleaseOrchestrator.Infrastructure.Providers;
using ReleaseOrchestrator.Providers.Abstractions.Deploy;

namespace ReleaseOrchestrator.Infrastructure.Execution;

/// <summary>
/// Drives running rollouts to completion: it re-derives the ready frontier from the step rows on
/// every tick (so a restart resumes for free), takes the atomic per-(MR, environment) claim before
/// any deploy, runs the wave, and polls async deploys until they settle
///.
/// </summary>
/// <remarks>
/// Registered in every replica but gated on a lease, so one coordinator drives at a time. Correctness
/// rests on the claim plus the step state machine, not on the lease -- the lease is only for liveness.
///
/// NOT VERIFIED end-to-end against a live GitLab or RabbitMQ; the deploy calls go through the
/// (also unverified) GitLab strategies. The state machine and claim logic are exercised by tests on
/// SQLite; the external deploy path needs a live check.
/// </remarks>
public class RolloutCoordinator(
    IServiceScopeFactory scopeFactory,
    IDistributedLease lease,
    IOptions<RolloutExecutionOptions> options,
    TimeProvider clock,
    ILogger<RolloutCoordinator> logger) : BackgroundService
{
    private const string LeaseName = "rollout-coordinator";

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled)
        {
            logger.LogInformation("Rollout coordinator is disabled");
            return;
        }

        var interval = TimeSpan.FromSeconds(Math.Max(options.Value.IntervalSeconds, 1));
        using var timer = new PeriodicTimer(interval, clock);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await timer.WaitForNextTickAsync(stoppingToken)) return;

                await using var held = await lease.TryAcquireAsync(LeaseName, interval, stoppingToken);
                if (held is null) continue;

                await DriveAllAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Rollout coordinator pass failed");
            }
        }
    }

    private async Task DriveAllAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var active = await db.Rollouts
            .Where(r => r.Status == RolloutStatus.Running || r.Status == RolloutStatus.Cancelling)
            .Select(r => r.Id)
            .ToListAsync(ct);

        foreach (var id in active)
        {
            try
            {
                await DriveOneAsync(scope.ServiceProvider, id, ct);
            }
            catch (DbUpdateConcurrencyException ex)
            {
                // Another replica drove this rollout in the same tick and won. Ordinary under a
                // shared lease, and the next tick re-reads: logged at Warning so it is visible, but
                // not as an error, which would train an operator to ignore the error log.
                logger.LogWarning(ex, "Rollout {Rollout} was updated concurrently; retrying on the next pass", id);
                ResetContext(scope, id);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Driving rollout {Rollout} failed", id);
                ResetContext(scope, id);
            }
        }
    }

    /// <summary>
    /// Discards the failed unit of work so the next rollout in the pass starts clean.
    /// </summary>
    /// <remarks>
    /// The scope — and therefore the <c>AppDbContext</c> — is shared by every rollout in one pass.
    /// A failed <c>SaveChanges</c> leaves its entities tracked as Modified, so the very next
    /// rollout's save re-submits them and fails the same way: one concurrency loss silently
    /// cascaded into every remaining rollout in the pass, each logged as its own unrelated error.
    /// </remarks>
    private static void ResetContext(IServiceScope scope, Guid rolloutId)
    {
        try
        {
            scope.ServiceProvider.GetRequiredService<AppDbContext>().ChangeTracker.Clear();
        }
        catch
        {
            // Nothing useful to do: the next pass builds a fresh scope regardless, and throwing from
            // a cleanup path would abort the remaining rollouts this call was trying to protect.
        }
    }

    private async Task DriveOneAsync(IServiceProvider sp, Guid rolloutId, CancellationToken ct)
    {
        var db = sp.GetRequiredService<AppDbContext>();

        var rollout = await db.Rollouts
            .Include(r => r.Environment)
            .Include(r => r.Steps).ThenInclude(s => s.MergeRequest).ThenInclude(m => m.Repository).ThenInclude(rep => rep.Connection)
            .FirstOrDefaultAsync(r => r.Id == rolloutId, ct);
        if (rollout is null) return;

        var statusBefore = rollout.Status;

        // Recover any step stranded mid-deploy by a prior crashed pass or lease hand-off, before
        // anything else looks at the frontier.
        await RecoverStrandedStepsAsync(db, rollout, ct);

        // Poll in-flight async deploys first, so a wave can advance in the same tick it settles.
        foreach (var step in rollout.Steps.Where(s => s.State == RolloutStepState.Awaiting).ToList())
            await PollAsync(sp, db, rollout, step, ct);

        if (rollout.Status == RolloutStatus.Running)
            await DispatchWaveAsync(sp, db, rollout, ct);

        await SettleRolloutAsync(db, rollout, ct);

        // One place records a status transition, and it is here rather than at each site that
        // assigns one. The dominant pause is written by DispatchWaveAsync, which stops the run while
        // later waves are still Pending -- SettleRolloutAsync's pause branch requires nothing
        // Pending, so an event emitted there would miss the ordinary fail-stop entirely. Comparing
        // against the status captured on entry catches every transition regardless of which branch
        // produced it, including a future one nobody remembers to instrument.
        //
        // Succeeded is excluded: SettleRolloutAsync already writes it, and duplicating it here would
        // put two events on one transition.
        if (rollout.Status != statusBefore && rollout.Status is RolloutStatus.Paused or RolloutStatus.Cancelled)
            db.RolloutEvents.Add(new RolloutEvent
            {
                Id = Guid.NewGuid(),
                RolloutId = rollout.Id,
                Kind = rollout.Status == RolloutStatus.Paused ? RolloutEventKinds.Paused : RolloutEventKinds.Cancelled,
                ActorKind = ActorKinds.Coordinator,
                PayloadJson = JsonSerializer.Serialize(new
                {
                    failed = rollout.Steps.Count(s => s.State == RolloutStepState.Failed),
                    total = rollout.Steps.Count
                }),
                At = clock.GetUtcNow().UtcDateTime
            });

        await db.SaveChangesAsync(ct);

        // On a terminal transition, run the matching action bindings -- off the deploy path, so a
        // failing notification never affects the rollout.
        if (rollout.Status != statusBefore
            && rollout.Status is RolloutStatus.Succeeded or RolloutStatus.Paused or RolloutStatus.Cancelled)
            await DispatchRolloutEventAsync(sp, rollout, ct);
    }

    private static async Task DispatchRolloutEventAsync(IServiceProvider sp, Rollout rollout, CancellationToken ct)
    {
        var db = sp.GetRequiredService<AppDbContext>();
        var dispatcher = sp.GetRequiredService<ActionDispatcher>();

        var task = await db.Tasks
            .Where(t => t.Id == rollout.TargetTaskId)
            .Select(t => new { t.ExternalId, TrackerName = t.TrackerConnection != null ? t.TrackerConnection.Name : "" })
            .FirstOrDefaultAsync(ct);
        var environmentKey = await db.DeploymentEnvironments
            .Where(e => e.Id == rollout.EnvironmentId)
            .Select(e => e.Key)
            .FirstOrDefaultAsync(ct);

        var payload = new Dictionary<string, string>
        {
            ["task"] = task?.ExternalId ?? string.Empty,
            ["trackerConnectionName"] = task?.TrackerName ?? string.Empty,
            ["environment"] = environmentKey ?? string.Empty,
            ["status"] = rollout.Status.ToString()
        };

        var eventType = rollout.Status switch
        {
            RolloutStatus.Succeeded => "RolloutSucceeded",
            RolloutStatus.Paused => "RolloutFailed",
            RolloutStatus.Cancelled => "RolloutCancelled",
            _ => "RolloutChanged"
        };

        await dispatcher.DispatchAsync(eventType, payload, ct);
    }

    private async Task DispatchWaveAsync(IServiceProvider sp, AppDbContext db, Rollout rollout, CancellationToken ct)
    {
        // A wave can start only when every earlier wave is terminal-success. A failed earlier step
        // pauses the run (fail-stop).
        var byWave = rollout.Steps.GroupBy(s => s.Wave).OrderBy(g => g.Key);
        foreach (var wave in byWave)
        {
            var pending = wave.Where(s => s.State == RolloutStepState.Pending).ToList();
            var done = wave.All(s => s.State is RolloutStepState.Succeeded or RolloutStepState.Skipped);
            if (done) continue; // this wave is finished; look at the next

            if (wave.Any(s => s.State == RolloutStepState.Failed))
            {
                rollout.Status = RolloutStatus.Paused;
                return;
            }

            // Dispatch the pending steps of the first not-done wave, up to the concurrency cap, then
            // stop: later waves must wait for this one.
            foreach (var step in pending.Take(options.Value.WaveConcurrency))
                await StartStepAsync(sp, db, rollout, step, ct);
            return;
        }
    }

    private async Task StartStepAsync(IServiceProvider sp, AppDbContext db, Rollout rollout, RolloutStep step, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var mrId = step.MergeRequestId;
        var envId = rollout.EnvironmentId;

        // The atomic single-writer claim. Ensure the row exists, then compare-and-set: only the
        // update that flips NotStarted/Released to Claiming wins.
        await EnsureClaimRowAsync(db, mrId, envId, ct);
        var won = await db.MrDeployClaims
            .Where(c => c.MergeRequestId == mrId && c.EnvironmentId == envId
                        && (c.State == ClaimState.NotStarted || c.State == ClaimState.Released))
            .ExecuteUpdateAsync(s => s
                .SetProperty(c => c.State, ClaimState.Claiming)
                .SetProperty(c => c.OwnerRolloutId, rollout.Id)
                .SetProperty(c => c.ExpiresAt, now.AddMinutes(30)), ct);

        if (won == 0)
        {
            // Someone else holds it. If they finished, short-circuit to Skipped; else wait a tick.
            var deployed = await IsDeployedAsync(db, mrId, envId, ct);
            if (deployed)
            {
                step.State = RolloutStepState.Skipped;
                step.FinishedAt = now;
            }
            return;
        }

        step.State = RolloutStepState.Deploying;
        step.StartedAt ??= now;
        step.AttemptCount++;
        await UpsertDeploymentStateAsync(db, mrId, envId, DeploymentState.Deploying, now, ct);
        await db.SaveChangesAsync(ct); // persist Deploying + the claim before the external call

        DeployResult result;
        try
        {
            var strategy = sp.GetRequiredService<IDeployStrategyFactory>().Resolve(step.DeployStrategyKey);
            result = await strategy.StartAsync(BuildContext(sp, rollout, step), ct);
        }
        catch (Exception ex)
        {
            await FailStepAsync(db, rollout, step, $"Deploy start threw: {ex.Message}", ct);
            return;
        }

        await ApplyResultAsync(db, rollout, step, result, ct);
    }

    private async Task PollAsync(IServiceProvider sp, AppDbContext db, Rollout rollout, RolloutStep step, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(step.ExternalRef)) return;

        // An in-flight deploy that has not settled within the deadline is given up, whether it kept
        // reporting Awaiting (a stuck pipeline) or its polls kept throwing (a persistent outage) -- so
        // an in-flight step can never re-poll forever and hang the rollout past fail-stop.
        if (IsPastDeadline(step))
        {
            await FailInFlightAsync(db, rollout, step,
                $"Deploy did not settle within {options.Value.MaxDeployMinutes} min; giving up.", ct);
            return;
        }

        DeployResult result;
        try
        {
            var strategy = sp.GetRequiredService<IDeployStrategyFactory>().Resolve(step.DeployStrategyKey);
            result = await strategy.PollAsync(BuildContext(sp, rollout, step), step.ExternalRef, ct);
        }
        catch (Exception ex)
        {
            // The POLL itself threw (a transient outage): the deploy's outcome is unknown and it may
            // still be running, so it must NOT be restarted -- that would deploy the same MR twice.
            // Re-poll next tick, keeping the claim and external ref; the deadline above bounds it. This
            // is distinct from ApplyResultAsync's default branch, where the poll SUCCEEDED and reported
            // a genuine failure -- that goes to FailStepAsync and fail-stops.
            logger.LogWarning(ex, "Rollout {Rollout} step {Step} poll failed transiently; will re-poll.", rollout.Id, step.Id);
            step.LastError = ex.Message;
            step.State = RolloutStepState.Awaiting;
            return;
        }

        await ApplyResultAsync(db, rollout, step, result, ct);
    }

    private async Task ApplyResultAsync(AppDbContext db, Rollout rollout, RolloutStep step, DeployResult result, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        switch (result.Outcome)
        {
            case DeployOutcome.Succeeded:
            case DeployOutcome.AlreadyDone:
                step.State = RolloutStepState.Succeeded;
                step.FinishedAt = now;
                step.LastError = null;
                await UpsertDeploymentStateAsync(db, step.MergeRequestId, rollout.EnvironmentId, DeploymentState.Deployed, now, ct);
                await ReleaseClaimAsync(db, step.MergeRequestId, rollout.EnvironmentId, ct);
                RecordAttempt(db, step, "Succeeded", result.Message, now);
                break;

            case DeployOutcome.Awaiting:
                step.State = RolloutStepState.Awaiting;
                step.ExternalRef = result.ExternalRef;
                RecordAttempt(db, step, "Awaiting", result.Message, now);
                break;

            default:
                await FailStepAsync(db, rollout, step, result.Message ?? "Deploy failed.", ct);
                break;
        }
    }

    /// <summary>
    /// Handles a KNOWN deploy failure: the deploy reported failure, or its start threw before it
    /// began. Reached only when the deploy is not running -- so re-triggering it is a retry, never a
    /// double-deploy. A transient POLL failure of an in-flight deploy does NOT come here; it re-polls.
    /// </summary>
    private async Task FailStepAsync(AppDbContext db, Rollout rollout, RolloutStep step, string message, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        await ReleaseClaimAsync(db, step.MergeRequestId, rollout.EnvironmentId, ct);
        RecordAttempt(db, step, "Failed", message, now);
        step.LastError = message;

        if (step.AttemptCount < options.Value.MaxAttempts)
        {
            // Retry the deploy from the start on the next tick. No backoff timer in this cut; a
            // persistent failure still stops at MaxAttempts and pauses the run (fail-stop).
            step.State = RolloutStepState.Pending;
        }
        else
        {
            step.State = RolloutStepState.Failed;
            step.FinishedAt = now;
            await UpsertDeploymentStateAsync(db, step.MergeRequestId, rollout.EnvironmentId, DeploymentState.Failed, now, ct);
        }
    }

    /// <summary>
    /// Gives up an in-flight deploy that never settled (deadline reached): fails the step and releases
    /// the claim WITHOUT retrying -- the deploy may still be running, so it is not restarted.
    /// </summary>
    private async Task FailInFlightAsync(AppDbContext db, Rollout rollout, RolloutStep step, string message, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        await ReleaseClaimAsync(db, step.MergeRequestId, rollout.EnvironmentId, ct);
        step.State = RolloutStepState.Failed;
        step.FinishedAt = now;
        step.LastError = message;
        await UpsertDeploymentStateAsync(db, step.MergeRequestId, rollout.EnvironmentId, DeploymentState.Failed, now, ct);
        RecordAttempt(db, step, "Failed", message, now);
    }

    /// <summary>True when an in-flight step has run past <see cref="RolloutExecutionOptions.MaxDeployMinutes"/>.</summary>
    private bool IsPastDeadline(RolloutStep step) =>
        step.StartedAt is { } startedAt
        && clock.GetUtcNow().UtcDateTime - startedAt > TimeSpan.FromMinutes(options.Value.MaxDeployMinutes);

    /// <summary>
    /// Fails any step left in <see cref="RolloutStepState.Deploying"/> by a crash or lease hand-off.
    /// </summary>
    /// <remarks>
    /// A Deploying step committed its intent to deploy (line before the external call) but recorded
    /// no outcome, and neither the poll frontier (Awaiting) nor the dispatch frontier (Pending)
    /// advances it -- so it would hang the rollout forever and leak its claim. Whether the external
    /// call actually fired is unknown, so it is not silently re-deployed (that risks a double-deploy
    /// of a non-idempotent strategy): the step is failed and its claim released, and the rollout
    /// pauses for an operator to verify the target and retry the step.
    /// </remarks>
    private async Task RecoverStrandedStepsAsync(AppDbContext db, Rollout rollout, CancellationToken ct)
    {
        var stranded = rollout.Steps.Where(s => s.State == RolloutStepState.Deploying).ToList();
        if (stranded.Count == 0) return;

        var now = clock.GetUtcNow().UtcDateTime;
        const string message = "Interrupted mid-deploy by a restart; the outcome is unknown. Verify the target and retry this step.";

        foreach (var step in stranded)
        {
            await ReleaseClaimAsync(db, step.MergeRequestId, rollout.EnvironmentId, ct);
            step.State = RolloutStepState.Failed;
            step.FinishedAt = now;
            step.LastError = message;
            await UpsertDeploymentStateAsync(db, step.MergeRequestId, rollout.EnvironmentId, DeploymentState.Failed, now, ct);
            RecordAttempt(db, step, "Failed", message, now);
            logger.LogWarning(
                "Rollout {Rollout} step {Step} was stranded in Deploying (crash mid-deploy); marked Failed for operator retry.",
                rollout.Id, step.Id);
        }
    }

    private async Task SettleRolloutAsync(AppDbContext db, Rollout rollout, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var inFlight = rollout.Steps.Any(s => s.State is RolloutStepState.Deploying or RolloutStepState.Awaiting or RolloutStepState.Claimed);

        if (rollout.Status == RolloutStatus.Cancelling)
        {
            if (!inFlight) { rollout.Status = RolloutStatus.Cancelled; rollout.FinishedAt = now; }
            return;
        }

        if (rollout.Steps.All(s => s.State is RolloutStepState.Succeeded or RolloutStepState.Skipped))
        {
            rollout.Status = RolloutStatus.Succeeded;
            rollout.FinishedAt = now;
            db.RolloutEvents.Add(new RolloutEvent
            {
                Id = Guid.NewGuid(), RolloutId = rollout.Id, Kind = RolloutEventKinds.Succeeded,
                ActorKind = ActorKinds.Coordinator, At = now
            });
        }
        else if (!inFlight && rollout.Steps.Any(s => s.State == RolloutStepState.Failed)
                 && !rollout.Steps.Any(s => s.State == RolloutStepState.Pending))
        {
            rollout.Status = RolloutStatus.Paused;
        }
    }

    private DeployContext BuildContext(IServiceProvider sp, Rollout rollout, RolloutStep step)
    {
        var protector = sp.GetRequiredService<TokenProtector>();
        var repo = step.MergeRequest.Repository;
        var conn = repo.Connection;

        if (!Uri.TryCreate(conn.ApiUrl, UriKind.Absolute, out var apiUrl))
            throw new InvalidOperationException($"Connection '{conn.Name}' has an invalid ApiUrl.");

        // The settings frozen onto the step at launch, not re-read from the repository: the run
        // deploys with the configuration it started with. Secret keys the strategy declares are
        // decrypted here, the same treatment the connection token gets above -- without this a secret
        // setting would reach the strategy as ciphertext. The schema comes from the factory, whose
        // GetSettingsSchema existed for exactly this and had no caller until now.
        var schema = sp.GetRequiredService<IDeployStrategyFactory>().GetSettingsSchema(step.DeployStrategyKey);
        var settings = ProviderSettingsProtection.UnprotectForUse(step.DeploySettingsJson, schema, protector);

        return new DeployContext(
            ApiUrl: apiUrl,
            AccessToken: protector.Unprotect(conn.EncryptedAccessToken),
            ConnectionName: conn.Name,
            ProjectPath: repo.ExternalId,
            MergeRequestExternalId: step.MergeRequest.ExternalId,
            EnvironmentKey: rollout.Environment.Key,
            Settings: settings);
    }

    private static void RecordAttempt(AppDbContext db, RolloutStep step, string outcome, string? message, DateTime now) =>
        db.RolloutStepAttempts.Add(new RolloutStepAttempt
        {
            Id = Guid.NewGuid(), RolloutStepId = step.Id, AttemptNo = step.AttemptCount,
            Outcome = outcome, Message = message, At = now
        });

    private static async Task EnsureClaimRowAsync(AppDbContext db, Guid mrId, Guid envId, CancellationToken ct)
    {
        var exists = await db.MrDeployClaims.AnyAsync(c => c.MergeRequestId == mrId && c.EnvironmentId == envId, ct);
        if (exists) return;
        try
        {
            db.MrDeployClaims.Add(new MrDeployClaim { MergeRequestId = mrId, EnvironmentId = envId, State = ClaimState.NotStarted });
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // A concurrent driver created it first; the compare-and-set that follows still decides the owner.
            foreach (var e in db.ChangeTracker.Entries<MrDeployClaim>().ToList()) e.State = EntityState.Detached;
        }
    }

    private static async Task ReleaseClaimAsync(AppDbContext db, Guid mrId, Guid envId, CancellationToken ct) =>
        await db.MrDeployClaims
            .Where(c => c.MergeRequestId == mrId && c.EnvironmentId == envId)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.State, ClaimState.Released).SetProperty(c => c.OwnerRolloutId, (Guid?)null), ct);

    private static async Task<bool> IsDeployedAsync(AppDbContext db, Guid mrId, Guid envId, CancellationToken ct) =>
        await db.MrDeploymentStates.AnyAsync(
            s => s.MergeRequestId == mrId && s.EnvironmentId == envId
                 && (s.State == DeploymentState.Deployed || s.State == DeploymentState.Skipped), ct);

    private static async Task UpsertDeploymentStateAsync(AppDbContext db, Guid mrId, Guid envId, DeploymentState state, DateTime now, CancellationToken ct)
    {
        var existing = await db.MrDeploymentStates.FirstOrDefaultAsync(s => s.MergeRequestId == mrId && s.EnvironmentId == envId, ct);
        if (existing is null)
            db.MrDeploymentStates.Add(new MrDeploymentState { MergeRequestId = mrId, EnvironmentId = envId, State = state, UpdatedAt = now });
        else { existing.State = state; existing.UpdatedAt = now; }
    }
}
