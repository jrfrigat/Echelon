namespace ReleaseOrchestrator.Core.Enums;

/// <summary>
/// The state of the single-writer deploy claim for a (merge request, environment) pair.
/// </summary>
/// <remarks>
/// The atomic guard against double-deploy: a step compare-and-sets <see cref="NotStarted"/> to
/// <see cref="Claiming"/> before any external call, so only one rollout can be mid-deploy for a
/// given merge request in a given environment.
/// </remarks>
public enum ClaimState
{
    /// <summary>Free to claim.</summary>
    NotStarted = 1,

    /// <summary>Held by the owning rollout while it deploys.</summary>
    Claiming = 2,

    /// <summary>Released after the deploy settled; a later rollout may claim again.</summary>
    Released = 3
}
