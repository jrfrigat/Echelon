using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;
using ReleaseOrchestrator.Core.Enums;

namespace ReleaseOrchestrator.Infrastructure.Persistence.Models;

/// <summary>
/// How one repository is deployed to one environment: which strategy, with which settings, and
/// whether a merge request already deployed there may be deployed again.
/// </summary>
/// <remarks>
/// <para>
/// This is the entity that lets the same repository deploy differently per environment - merge the
/// merge request on production, trigger a pipeline stage on a test rig - which the operator asked for
/// and which a single strategy key on the repository could not express. It is also what unblocks
/// launching at all: a rollout resolves a step's strategy from the target for its (repository,
/// environment), and a task could not launch while nothing set one.
/// </para>
/// <para>
/// The repository still carries a strategy key of its own, used as the default when a repository
/// deploys the same way everywhere; a target overrides it for the environments that differ. So the
/// common case needs no target per environment, and only the environments that diverge get a row.
/// </para>
/// </remarks>
[Index(nameof(RepositoryId), nameof(EnvironmentId), IsUnique = true, Name = "IX_RepositoryDeployTarget_Repository_Environment")]
public class RepositoryDeployTarget
{
    /// <summary>Primary key.</summary>
    [Key]
    public Guid Id { get; set; }

    /// <summary>The repository this configures.</summary>
    public Guid RepositoryId { get; set; }

    /// <summary>The environment this configures it for.</summary>
    public Guid EnvironmentId { get; set; }

    /// <summary>
    /// The deploy strategy for this repository in this environment - the key of an
    /// <c>IDeployStrategy</c>, e.g. <c>gitlab-merge</c> or <c>gitlab-pipeline</c>.
    /// </summary>
    [Required, MaxLength(100)]
    public string DeployStrategyKey { get; set; } = string.Empty;

    /// <summary>
    /// Strategy-specific settings for this repository in this environment, as JSON. Secret keys the
    /// strategy declares are encrypted at rest here, the same treatment a connection token gets.
    /// </summary>
    public string? DeploySettingsJson { get; set; }

    /// <summary>
    /// Whether a merge request already deployed here may be deployed again. Defaults to
    /// <see cref="Core.Enums.RedeployPolicy.Once"/> - production's answer, and the safe one - so a
    /// target created without a deliberate choice does not permit repeats.
    /// </summary>
    public RedeployPolicy RedeployPolicy { get; set; } = RedeployPolicy.Once;

    /// <summary>
    /// The readiness rule this repository uses for this environment, overriding the environment's
    /// default; null means fall back to that default.
    /// </summary>
    /// <remarks>
    /// This is the per-repository override the operator asked for: a repository that must clear a
    /// stricter (or looser) bar than the rest of the environment points at its own named rule here.
    /// It resolves ahead of <see cref="DeploymentEnvironment.ReadinessRuleId"/> and behind a manual
    /// pin.
    /// </remarks>
    public Guid? ReadinessRuleId { get; set; }

    /// <summary>The readiness rule override. Restrict: a rule in use cannot be deleted out from under a target.</summary>
    [ForeignKey(nameof(ReadinessRuleId))]
    [InverseProperty(nameof(Models.ReadinessRule.DeployTargets))]
    [DeleteBehavior(DeleteBehavior.Restrict)]
    public ReadinessRule? ReadinessRule { get; set; }

    /// <summary>The repository. Cascade: the target is owned by it and is meaningless without it.</summary>
    [ForeignKey(nameof(RepositoryId))]
    [InverseProperty(nameof(Models.Repository.DeployTargets))]
    [DeleteBehavior(DeleteBehavior.Cascade)]
    public Repository Repository { get; set; } = null!;

    /// <summary>
    /// The environment. Restrict: deleting an environment that still has deploy targets is blocked
    /// so the configuration is removed deliberately, not silently - and it keeps this table to a
    /// single cascade path, which SQL Server requires.
    /// </summary>
    [ForeignKey(nameof(EnvironmentId))]
    [InverseProperty(nameof(DeploymentEnvironment.DeployTargets))]
    [DeleteBehavior(DeleteBehavior.Restrict)]
    public DeploymentEnvironment Environment { get; set; } = null!;
}
