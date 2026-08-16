using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace ReleaseOrchestrator.Infrastructure.Persistence.Models;

/// <summary>A repository served by a VCS connection.</summary>
// Natural key: VcsService and the YAML import both resolve a repository by this pair.
[Index(nameof(ConnectionId), nameof(ExternalId), IsUnique = true, Name = "IX_Repository_ConnectionId_ExternalId")]
public class Repository
{
    /// <summary>Primary key.</summary>
    [Key]
    public Guid Id { get; set; }

    /// <summary>Operator-facing name. Appears in plans and conflict messages.</summary>
    [Required, MaxLength(300)]
    public string Name { get; set; } = string.Empty;

    /// <summary>The VCS's own path, e.g. <c>group/project</c>.</summary>
    [Required, MaxLength(500)]
    public string ExternalId { get; set; } = string.Empty;

    /// <summary>The VCS connection serving it.</summary>
    public Guid ConnectionId { get; set; }

    /// <summary>
    /// Which tracker this repository's branch issue keys belong to.
    ///
    /// Repositories hang off a VCS connection and tasks off a tracker connection, with nothing
    /// joining them: an issue key parsed from a branch could only be matched globally, so in a
    /// multi-tracker setup - which this product exists to serve - the same key in two trackers
    /// made the link ambiguous and it was dropped rather than guessed at.
    ///
    /// Null keeps the old global match, which is fine for a single tracker.
    /// </summary>
    public Guid? TrackerConnectionId { get; set; }

    /// <summary>Its VCS connection. Restrict: a connection with repositories is not deleted out from under them.</summary>
    [ForeignKey(nameof(ConnectionId))]
    [InverseProperty(nameof(VcsConnection.Repositories))]
    [DeleteBehavior(DeleteBehavior.Restrict)]
    public VcsConnection Connection { get; set; } = null!;

    /// <summary>Its tracker, if one is bound.</summary>
    [ForeignKey(nameof(TrackerConnectionId))]
    [InverseProperty(nameof(Models.TrackerConnection.Repositories))]
    [DeleteBehavior(DeleteBehavior.Restrict)]
    public TrackerConnection? TrackerConnection { get; set; }

    /// <summary>Merge requests in this repository.</summary>
    [InverseProperty(nameof(MergeRequest.Repository))]
    public ICollection<MergeRequest> MergeRequests { get; set; } = [];

    /// <summary>
    /// How this repository is deployed -- the key of an <c>IDeployStrategy</c> (e.g. <c>gitlab-merge</c>
    /// or <c>gitlab-pipeline</c>). Null until an operator picks one; a rollout step cannot deploy
    /// the repository without it. Overridable per plan item.
    /// </summary>
    [MaxLength(100)]
    public string? DeployStrategyKey { get; set; }

    /// <summary>Strategy-specific settings for this repository, as JSON (schema-declared by the strategy).</summary>
    public string? DeployStrategySettingsJson { get; set; }

    /// <summary>Per-environment deploy configuration, overriding <see cref="DeployStrategyKey"/> where an environment differs.</summary>
    [InverseProperty(nameof(RepositoryDeployTarget.Repository))]
    public ICollection<RepositoryDeployTarget> DeployTargets { get; set; } = [];

    /// <summary>Repository-ordering links where this repository waits (it deploys after their targets).</summary>
    [InverseProperty(nameof(RepositoryDependency.FromRepository))]
    public ICollection<RepositoryDependency> DependsOn { get; set; } = [];

    /// <summary>Repository-ordering links where this repository is waited on.</summary>
    [InverseProperty(nameof(RepositoryDependency.ToRepository))]
    public ICollection<RepositoryDependency> RequiredBy { get; set; } = [];
}
