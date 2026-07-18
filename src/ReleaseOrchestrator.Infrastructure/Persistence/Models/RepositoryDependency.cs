using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;
using ReleaseOrchestrator.Core.Enums;

namespace ReleaseOrchestrator.Infrastructure.Persistence.Models;

/// <summary>
/// "<see cref="FromRepository"/> deploys after <see cref="ToRepository"/>."
/// </summary>
/// <remarks>
/// The editable, provider-neutral replacement for stacks: instead of grouping repositories and
/// linking the groups, an operator states repository ordering directly (repo -> repo). A task
/// usually spans several repositories -- a migration repo, a backend, a frontend -- and these rules
/// give their merge requests a default order within the task. Same direction convention as
/// <see cref="TaskDependency"/>: the row names who waits, then who is waited on. Reversing it
/// reverses the deploy order of everything between the two repositories.
///
/// Introduced alongside the stack model, which the planner still reads; stacks are removed in the
/// cleanup phase (docs/issues/009-admin-and-migration.md). The Hard/Soft criticality is shared with
/// the stack model via <see cref="StackDependencyType"/> for now; it is renamed when stacks go.
/// </remarks>
[Index(nameof(FromRepositoryId), nameof(ToRepositoryId), IsUnique = true, Name = "IX_RepositoryDependency_From_To")]
public class RepositoryDependency
{
    /// <summary>Primary key.</summary>
    [Key]
    public Guid Id { get; set; }

    /// <summary>The repository that waits.</summary>
    public Guid FromRepositoryId { get; set; }

    /// <summary>The repository waited on.</summary>
    public Guid ToRepositoryId { get; set; }

    /// <summary>Hard links are never dropped to break a cycle; soft ones are dropped first.</summary>
    public StackDependencyType Type { get; set; }

    /// <summary>The waiting repository. Restrict: a repository in a link is not deleted out from under it.</summary>
    [ForeignKey(nameof(FromRepositoryId))]
    [InverseProperty(nameof(Models.Repository.DependsOn))]
    [DeleteBehavior(DeleteBehavior.Restrict)]
    public Repository FromRepository { get; set; } = null!;

    /// <summary>The repository waited on. Restrict, for the same reason.</summary>
    [ForeignKey(nameof(ToRepositoryId))]
    [InverseProperty(nameof(Models.Repository.RequiredBy))]
    [DeleteBehavior(DeleteBehavior.Restrict)]
    public Repository ToRepository { get; set; } = null!;
}
