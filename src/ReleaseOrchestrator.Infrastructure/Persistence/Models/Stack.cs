using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace ReleaseOrchestrator.Infrastructure.Persistence.Models;

/// <summary>
/// A group of repositories that deploy together, e.g. "database" or "backend".
/// </summary>
/// <remarks>
/// Stacks are the second source of ordering, alongside task dependencies: a link between two of
/// them says every merge request in one deploys before every merge request in the other.
/// </remarks>
[Index(nameof(Name), IsUnique = true)]
public class Stack
{
    /// <summary>Primary key.</summary>
    [Key]
    public Guid Id { get; set; }

    /// <summary>Operator-facing name; unique.</summary>
    [Required, MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    /// <summary>Repositories in this stack.</summary>
    [InverseProperty(nameof(RepositoryStack.Stack))]
    public ICollection<RepositoryStack> RepositoryStacks { get; set; } = [];

    /// <summary>Links where this stack waits — it deploys after their targets.</summary>
    [InverseProperty(nameof(StackDependency.FromStack))]
    public ICollection<StackDependency> DependentOn { get; set; } = [];

    /// <summary>Links where this stack is waited on — it deploys before their sources.</summary>
    [InverseProperty(nameof(StackDependency.ToStack))]
    public ICollection<StackDependency> RequiredBy { get; set; } = [];
}
