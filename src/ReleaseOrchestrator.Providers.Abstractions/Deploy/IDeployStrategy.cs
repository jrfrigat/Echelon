namespace ReleaseOrchestrator.Providers.Abstractions.Deploy;

/// <summary>
/// How one repository is deployed: merge the request, trigger a pipeline, or something custom.
/// </summary>
/// <remarks>
/// The pluggable deploy axis. A strategy is a keyed service selected by
/// <c>Repository.DeployStrategyKey</c> (overridable per plan item); adding one is a new class plus a
/// registration, with no change to the core. The two-phase Start + Poll shape makes an async
/// pipeline deploy resumable, and <see cref="ReconcileAsync"/> lets a resumed step re-attach to an
/// in-flight deploy instead of triggering it twice.
///
/// Idempotency is a contract obligation: <see cref="StartAsync"/> on an already-deployed target must
/// return <see cref="DeployOutcome.AlreadyDone"/>, not deploy again. It is testable, not enforced by
/// the type system (docs/issues/007-execution-engine.md).
/// </remarks>
public interface IDeployStrategy
{
    /// <summary>The settings this strategy accepts, so a UI can render a form and the API can validate.</summary>
    IReadOnlyList<ProviderSettingSchema> SettingsSchema { get; }

    /// <summary>Starts the deploy. Returns <see cref="DeployOutcome.Awaiting"/> with an external reference for async work.</summary>
    /// <param name="context">The target and credentials.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<DeployResult> StartAsync(DeployContext context, CancellationToken ct);

    /// <summary>Polls an in-flight async deploy by its external reference.</summary>
    /// <param name="context">The target and credentials.</param>
    /// <param name="externalRef">The reference returned by <see cref="StartAsync"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<DeployResult> PollAsync(DeployContext context, string externalRef, CancellationToken ct);

    /// <summary>Best-effort cancellation of an in-flight async deploy.</summary>
    /// <param name="context">The target and credentials.</param>
    /// <param name="externalRef">The reference returned by <see cref="StartAsync"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    Task CancelAsync(DeployContext context, string externalRef, CancellationToken ct);

    /// <summary>
    /// After a crash, checks whether the target is already deployed (or deploying) so a resumed step
    /// re-attaches instead of re-triggering. Returns <c>null</c> when nothing is in flight.
    /// </summary>
    /// <param name="context">The target and credentials.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<DeployResult?> ReconcileAsync(DeployContext context, CancellationToken ct);
}
