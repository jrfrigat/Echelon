using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;
using ReleaseOrchestrator.Core.Enums;

namespace ReleaseOrchestrator.Infrastructure.Persistence.Models;

/// <summary>
/// A named, reusable readiness rule: a set of required signals and how many must match. Created once
/// and then assigned to an environment as its default, or to a repository for one environment as an
/// override.
/// </summary>
/// <remarks>
/// <para>
/// This replaced the readiness that lived inline on the environment (a label set plus a mode). Inline,
/// every environment restated the same policy and a repository could not deviate; as a named entity,
/// "prod-strict" is written once and pointed at from wherever it applies. What a rule matches against
/// is signals - <c>label:*</c>, <c>mr-status:*</c>, <c>pipeline:*</c> - not labels alone, so a project
/// that gates on a pipeline result or a merge state expresses it in the same rule language.
/// </para>
/// <para>
/// <see cref="RequiredSignals"/> is one canonical string, like <see cref="MergeRequest.Labels"/>, for
/// the same reasons: the gate loads the rule and compares in memory, and a plain column needs no child
/// table or cascade. <see cref="Mode"/> is only <see cref="ReadyRule.AnyOf"/> or
/// <see cref="ReadyRule.AllOf"/> - a named rule with no gate would be a rule that is not one; "no gate"
/// is the absence of an assigned rule, not a rule you can name.
/// </para>
/// </remarks>
[Index(nameof(Name), IsUnique = true, Name = "IX_ReadinessRule_Name")]
public class ReadinessRule
{
    /// <summary>Primary key.</summary>
    [Key]
    public Guid Id { get; set; }

    /// <summary>Operator-facing name; unique, and how an environment or repository refers to the rule.</summary>
    [Required, MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    /// <summary>Whether a merge request must carry any one required signal (<see cref="ReadyRule.AnyOf"/>) or all of them (<see cref="ReadyRule.AllOf"/>).</summary>
    public ReadyRule Mode { get; set; } = ReadyRule.AllOf;

    /// <summary>
    /// The signals a merge request must carry to satisfy this rule, canonical (comma-joined). Each is a
    /// <c>kind:value</c> token - <c>label:ready-for-prod</c>, <c>pipeline:success</c>. Empty admits
    /// nothing, which the resolver guards, so a rule cannot be saved into "matches everything".
    /// </summary>
    [Required, MaxLength(2000)]
    public string RequiredSignals { get; set; } = string.Empty;

    /// <summary>Environments that use this rule as their default.</summary>
    [InverseProperty(nameof(DeploymentEnvironment.ReadinessRule))]
    public ICollection<DeploymentEnvironment> Environments { get; set; } = [];

    /// <summary>Repository deploy targets that use this rule, overriding their environment's default.</summary>
    [InverseProperty(nameof(RepositoryDeployTarget.ReadinessRule))]
    public ICollection<RepositoryDeployTarget> DeployTargets { get; set; } = [];
}
