using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace ReleaseOrchestrator.Infrastructure.Persistence.Models;

/// <summary>
/// A branch observed in a repository, and the task it names — the evidence that work for a task has
/// started even when no merge request has been raised for it.
/// </summary>
/// <remarks>
/// <para>
/// A plan is built from merge requests, so before this a task whose only artefact was a branch looked
/// finished: nothing to deploy, nothing to wait for. That is exactly backwards — a branch with no
/// merge request is work in progress, and rolling out its parent while it is unlanded ships an
/// incomplete change. This row is what lets the launch guard see that work.
/// </para>
/// <para>
/// The link to the task is the connection's own linking rule (source + pattern), applied to the branch
/// name by the same <c>TaskKeyExtractor</c> the merge-request path uses, so branches and merge requests
/// can never disagree about which task they belong to. <see cref="TaskExternalId"/> rather than a
/// task id: a branch is often pushed before the task has been imported, and the row must survive that
/// ordering rather than be dropped for want of a foreign key.
/// </para>
/// </remarks>
[Index(nameof(RepositoryId), nameof(Name), IsUnique = true, Name = "IX_RepositoryBranch_RepositoryId_Name")]
[Index(nameof(TaskExternalId), Name = "IX_RepositoryBranch_TaskExternalId")]
public class RepositoryBranch
{
    /// <summary>Primary key.</summary>
    [Key]
    public Guid Id { get; set; }

    /// <summary>The repository the branch lives in.</summary>
    public Guid RepositoryId { get; set; }

    /// <summary>Navigation to the repository. Cascade: a repository's branches are meaningless without it.</summary>
    [ForeignKey(nameof(RepositoryId))]
    public Repository Repository { get; set; } = null!;

    /// <summary>The branch name, as the provider spells it.</summary>
    [Required, MaxLength(500)]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// The task key the branch names, per the connection's linking rule, or null when it names none.
    /// A branch that names no task blocks nothing — it cannot be attributed to any task's work.
    /// </summary>
    [MaxLength(100)]
    public string? TaskExternalId { get; set; }

    /// <summary>Whether the provider reports the branch as merged into the default branch.</summary>
    public bool IsMerged { get; set; }

    /// <summary>Whether it is the repository's default branch, which is never anybody's unlanded work.</summary>
    public bool IsDefault { get; set; }

    /// <summary>When this service first saw the branch. Always UTC.</summary>
    public DateTime FirstSeenAt { get; set; }

    /// <summary>When this service last saw it in a sweep. Always UTC.</summary>
    public DateTime LastSeenAt { get; set; }
}
