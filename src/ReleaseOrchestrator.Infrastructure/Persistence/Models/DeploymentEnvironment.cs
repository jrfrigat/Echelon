using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;
using ReleaseOrchestrator.Core.Enums;

namespace ReleaseOrchestrator.Infrastructure.Persistence.Models;

/// <summary>
/// A named deploy target -- "staging", "prod" -- into which a task's rollout runs.
/// </summary>
/// <remarks>
/// Named <c>DeploymentEnvironment</c> rather than <c>Environment</c> to avoid colliding with
/// <see cref="System.Environment"/>. Environment is an orthogonal dimension: the plan tree and its
/// ordering are the same in every environment; a rollout is scoped to one. <see cref="Order"/> sets
/// the promotion sequence (staging before prod) that the optional progression gate enforces at
/// launch (docs/issues/007-execution-engine.md).
/// </remarks>
[Index(nameof(Key), IsUnique = true, Name = "IX_DeploymentEnvironment_Key")]
public class DeploymentEnvironment
{
    /// <summary>Primary key.</summary>
    [Key]
    public Guid Id { get; set; }

    /// <summary>Stable key used in APIs and deploy strategy settings, e.g. <c>staging</c>. Unique.</summary>
    [Required, MaxLength(50)]
    public string Key { get; set; } = string.Empty;

    /// <summary>Operator-facing name.</summary>
    [Required, MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    /// <summary>Promotion sequence: a lower order deploys first. Used by the progression gate.</summary>
    public int Order { get; set; }

    /// <summary>Whether rollouts may target this environment.</summary>
    public bool IsEnabled { get; set; } = true;

    /// <summary>
    /// How this environment decides a merge request is ready for it.
    /// </summary>
    /// <remarks>
    /// Defaults to <see cref="Core.Enums.ReadyRule.NoGate"/>, which is the behaviour that existed
    /// before there was a gate: a merge request that is ready-for-deploy may enter. So an existing
    /// environment keeps working unchanged until an operator deliberately gates it. Stored as text
    /// (see the configuration) because the enum has no zero member — an integer-defaulted column
    /// would hold a value it does not define.
    /// </remarks>
    public ReadyRule ReadyRule { get; set; } = ReadyRule.NoGate;

    /// <summary>
    /// The labels this environment's rule is evaluated against, canonical (comma-joined), empty when
    /// none are configured. Ignored when <see cref="ReadyRule"/> is <see cref="Core.Enums.ReadyRule.NoGate"/>.
    /// </summary>
    /// <remarks>
    /// One canonical string rather than a child table, for the same reasons as
    /// <see cref="MergeRequest.Labels"/>: the gate loads the environment and compares in memory
    /// (<see cref="Core.Parsing.ReadinessResolver"/>), and a plain column needs no cascade. A gate
    /// switched on with this empty admits nothing — the resolver guards that — so an environment
    /// cannot be gated by accident into admitting everything.
    /// </remarks>
    [Required, MaxLength(2000)]
    public string ReadyLabels { get; set; } = string.Empty;

    /// <summary>Per-repository deploy configuration scoped to this environment.</summary>
    [InverseProperty(nameof(RepositoryDeployTarget.Environment))]
    public ICollection<RepositoryDeployTarget> DeployTargets { get; set; } = [];

    /// <summary>Per-merge-request readiness overrides for this environment.</summary>
    [InverseProperty(nameof(MergeRequestReadinessPin.Environment))]
    public ICollection<MergeRequestReadinessPin> ReadinessPins { get; set; } = [];
}
