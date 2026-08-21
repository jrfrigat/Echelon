namespace Echelon.Application.DTOs;

/// <summary>
/// The bounded vocabulary of <see cref="ActorRef.Kind"/>.
/// </summary>
/// <remarks>
/// Constants rather than an enum, matching how <c>RolloutEvent.Kind</c> and
/// <c>RolloutStepAttempt.Outcome</c> already discriminate append-only rows: these values are
/// persisted as text and read back by a UI that maps them to a resource key, so a stable string is
/// the contract. An enum would add a conversion to configure and a migration to widen.
/// </remarks>
public static class ActorKinds
{
    /// <summary>A signed-in person with a usable object id.</summary>
    public const string User = "user";

    /// <summary>
    /// An application identity - a CI pipeline calling with a client credential. Distinguished from
    /// <see cref="User"/> because nothing validates scopes anywhere in this service, so an app
    /// registration granted a permission would otherwise be rendered as a person.
    /// </summary>
    public const string Service = "service";

    /// <summary>Authenticated, but the token carried no object id we could normalize. A person we cannot name.</summary>
    public const string Unidentified = "unidentified";

    /// <summary>A generic machine path, used where nothing more specific is known.</summary>
    public const string System = "system";

    /// <summary>An inbound webhook delivery from a VCS or tracker.</summary>
    public const string Webhook = "webhook";

    /// <summary>The polling ingestion path, for connections that cannot deliver webhooks.</summary>
    public const string Poller = "poller";

    /// <summary>The periodic reconciliation that re-reads tracker links no webhook announces.</summary>
    public const string Reconciler = "reconciler";

    /// <summary>The rollout coordinator: everything the execution engine does on its own timer.</summary>
    public const string Coordinator = "coordinator";

    /// <summary>Recovery of steps stranded by a restart or a lease handover, where the real outcome is unknown.</summary>
    public const string Recovery = "recovery";

    /// <summary>The archival background job.</summary>
    public const string Archiver = "archiver";
}
