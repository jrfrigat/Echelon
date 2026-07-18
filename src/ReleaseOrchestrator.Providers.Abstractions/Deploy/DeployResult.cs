namespace ReleaseOrchestrator.Providers.Abstractions.Deploy;

/// <summary>The outcome of a deploy strategy call.</summary>
/// <param name="Outcome">What happened.</param>
/// <param name="ExternalRef">
/// A provider handle for an async deploy (e.g. a pipeline id), returned with <see cref="DeployOutcome.Awaiting"/>
/// so the watcher can poll it and a resumed step can reconcile against it.
/// </param>
/// <param name="Message">An optional human-readable detail (a failure reason, a pipeline status).</param>
public sealed record DeployResult(DeployOutcome Outcome, string? ExternalRef = null, string? Message = null);
