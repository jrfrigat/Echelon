using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;
using ReleaseOrchestrator.Core.Enums;

namespace ReleaseOrchestrator.Infrastructure.Persistence.Models;

/// <summary>
/// The single-writer deploy claim for a (merge request, environment) pair.
/// </summary>
/// <remarks>
/// The atomic guard against double-deploy: a step compare-and-sets <see cref="ClaimState.NotStarted"/>
/// to <see cref="ClaimState.Claiming"/> in one UPDATE (rows-affected decides the winner) before any
/// external call. The pair is the key, so the same merge request can deploy to two environments at
/// once but never twice in one. Correctness rests on this
/// claim, not on lease expiry; <see cref="ExpiresAt"/> is only a liveness hint.
/// </remarks>
[PrimaryKey(nameof(MergeRequestId), nameof(EnvironmentId))]
public class MrDeployClaim
{
    /// <summary>The merge request.</summary>
    public Guid MergeRequestId { get; set; }

    /// <summary>The environment.</summary>
    public Guid EnvironmentId { get; set; }

    /// <summary>Whether the claim is free or held. See <see cref="ClaimState"/>.</summary>
    public ClaimState State { get; set; }

    /// <summary>The rollout holding the claim, if any. A soft reference (no FK): claims outlive runs.</summary>
    public Guid? OwnerRolloutId { get; set; }

    /// <summary>A liveness hint for a held claim; not the basis of correctness.</summary>
    public DateTime? ExpiresAt { get; set; }

    /// <summary>The merge request. Restrict.</summary>
    [ForeignKey(nameof(MergeRequestId))]
    [DeleteBehavior(DeleteBehavior.Restrict)]
    public MergeRequest MergeRequest { get; set; } = null!;

    /// <summary>The environment. Restrict.</summary>
    [ForeignKey(nameof(EnvironmentId))]
    [DeleteBehavior(DeleteBehavior.Restrict)]
    public DeploymentEnvironment Environment { get; set; } = null!;
}
