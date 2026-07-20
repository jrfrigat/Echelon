namespace ReleaseOrchestrator.Application.Auditing;

/// <summary>
/// The vocabulary of <c>RolloutEvent.Kind</c>.
/// </summary>
/// <remarks>
/// <para>
/// Constants because these are written as bare string literals at the call sites and read back by a
/// timeline that maps each one to a resource key. A typo in a literal is a row nothing renders and
/// nothing reports; a typo here does not compile.
/// </para>
/// <para>
/// The entity's own documentation used to advertise kinds — <c>Paused</c>, <c>Cancelled</c>,
/// <c>StepSucceeded</c> — that no code ever wrote. Anything named here must have a write site, and
/// anything written must be named here.
/// </para>
/// </remarks>
public static class RolloutEventKinds
{
    /// <summary>A rollout was launched and its steps materialised.</summary>
    public const string Launched = "Launched";

    /// <summary>
    /// A launch was requested for a run that was already live, so it returned the existing rollout.
    /// </summary>
    /// <remarks>
    /// Recorded because the request succeeds silently. Two operators pressing Launch a minute apart
    /// both get HTTP 200, the second one changes nothing, and without this the audit names only the
    /// first — on a production deploy the second may well be the person who actually drove it.
    /// </remarks>
    public const string LaunchCoalesced = "LaunchCoalesced";

    /// <summary>The run finished with every step succeeded or skipped.</summary>
    public const string Succeeded = "Succeeded";

    /// <summary>
    /// The run stopped because a step failed and will not proceed without an operator.
    /// </summary>
    public const string Paused = "Paused";

    /// <summary>
    /// An operator asked for cancellation while steps were still in flight, so the run entered
    /// Cancelling and waits for them to settle.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="Cancelled"/> because they are two different facts about two moments,
    /// with two different actors: a person asked, and later the coordinator finished. Writing
    /// <c>Cancelled</c> for both put two identical-looking entries on one timeline for what the
    /// operator experienced as a single action.
    /// </remarks>
    public const string CancelRequested = "CancelRequested";

    /// <summary>The run reached its cancelled end state.</summary>
    public const string Cancelled = "Cancelled";

    /// <summary>
    /// An operator reset a failed step so the coordinator would try it again.
    /// </summary>
    /// <remarks>
    /// Underivable from state: the retry sets the step back to Pending and clears its error, leaving
    /// nothing behind that says it ever failed or that anyone intervened.
    /// </remarks>
    public const string StepRetried = "StepRetried";

    /// <summary>
    /// An operator declared a step done without deploying it.
    /// </summary>
    /// <remarks>
    /// The most consequential manual action in the system — it marks the merge request deployed in
    /// that environment, which the readiness gate then trusts for every later rollout.
    /// </remarks>
    public const string StepSkipped = "StepSkipped";

    /// <summary>
    /// This orchestrator wrote a status back to the issue tracker.
    /// </summary>
    /// <remarks>
    /// Recorded so the write-back is visible as something the system did. The tracker will echo it
    /// back as an inbound webhook; the timeline shows both as the separate facts they are and does
    /// not claim the first caused the second — nothing correlates them, and Yandex deliveries carry
    /// no event id to deduplicate on.
    /// </remarks>
    public const string TrackerStatusPushed = "TrackerStatusPushed";
}
