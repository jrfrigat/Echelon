using Echelon.Application.DTOs;

namespace Echelon.Application.Services;

/// <summary>
/// Launches and drives per-task rollouts: readiness gates, materialisation into steps, and the
/// operator controls over a run (cancel / retry / skip). The step-by-step deploy is driven by the
/// coordinator background service.
/// </summary>
public interface IRolloutService
{
    /// <summary>
    /// Launches a rollout of a task into an environment: checks readiness and the mandatory-edge and
    /// environment-progression gates, freezes the plan, and materialises the ordered steps.
    /// </summary>
    /// <param name="taskId">The task to roll out.</param>
    /// <param name="environmentId">The target environment.</param>
    /// <param name="actor">Who launched it, for the audit trail.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <param name="redeploy">
    /// When true, a merge request already deployed to this environment is deployed again - but only
    /// where its repository's deploy target for this environment permits it
    /// (<c>RedeployPolicy.Always</c>). Two independent conditions, so neither a stray flag nor a
    /// standing policy redeploys on its own.
    /// </param>
    Task<RolloutDto> LaunchAsync(Guid taskId, Guid environmentId, ActorRef actor, bool redeploy = false, CancellationToken ct = default);

    /// <summary>The run and its steps, or <c>null</c> when it does not exist.</summary>
    /// <param name="rolloutId">The run id.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<RolloutDto?> GetAsync(Guid rolloutId, CancellationToken ct = default);

    /// <summary>Recent runs, newest first, optionally filtered to one task.</summary>
    /// <param name="taskId">The task to filter by, or <c>null</c> for all.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<RolloutSummaryDto>> ListAsync(Guid? taskId, CancellationToken ct = default);

    /// <summary>Requests cancellation: stops dispatching new steps and lets in-flight ones settle.</summary>
    /// <param name="rolloutId">The run id.</param>
    /// <param name="actor">Who cancelled it, for the audit trail.</param>
    /// <param name="ct">Cancellation token.</param>
    Task CancelAsync(Guid rolloutId, ActorRef actor, CancellationToken ct = default);

    /// <summary>Resets a failed step to pending so the coordinator retries it, and resumes a paused run.</summary>
    /// <param name="rolloutId">The run id.</param>
    /// <param name="stepId">The step id.</param>
    /// <param name="actor">Who retried it, for the audit trail. A retry changes no column a reader could attribute afterwards.</param>
    /// <param name="ct">Cancellation token.</param>
    Task RetryStepAsync(Guid rolloutId, Guid stepId, ActorRef actor, CancellationToken ct = default);

    /// <summary>Marks a step skipped (and its merge request skipped in the environment), and resumes a paused run.</summary>
    /// <param name="rolloutId">The run id.</param>
    /// <param name="stepId">The step id.</param>
    /// <param name="actor">
    /// Who skipped it. Declaring a merge request deployed without deploying it is the most
    /// consequential thing an operator can do here, and it is unattributed today.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    Task SkipStepAsync(Guid rolloutId, Guid stepId, ActorRef actor, CancellationToken ct = default);
}
