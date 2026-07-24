using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

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

    /// <summary>Per-repository deploy configuration scoped to this environment.</summary>
    [InverseProperty(nameof(RepositoryDeployTarget.Environment))]
    public ICollection<RepositoryDeployTarget> DeployTargets { get; set; } = [];
}
