using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;
using ReleaseOrchestrator.Core.Enums;

namespace ReleaseOrchestrator.Infrastructure.Persistence.Models;

/// <summary>
/// "<see cref="FromStack"/> deploys after <see cref="ToStack"/>."
/// </summary>
/// <remarks>
/// The direction reads the same way round as <see cref="TaskDependency"/>: the row names who
/// waits, then who is waited on. Reversing it reverses the deploy order of everything in both
/// stacks — a backend shipped before the migration it needs.
/// </remarks>
public class StackDependency
{
    /// <summary>Primary key.</summary>
    [Key]
    public Guid Id { get; set; }

    /// <summary>The stack that waits.</summary>
    public Guid FromStackId { get; set; }

    /// <summary>The stack waited on.</summary>
    public Guid ToStackId { get; set; }

    /// <summary>Hard links are never dropped to break a cycle; soft ones are dropped first.</summary>
    public StackDependencyType Type { get; set; }

    /// <summary>The waiting stack. Restrict: a stack in a link is not deleted out from under it.</summary>
    [ForeignKey(nameof(FromStackId))]
    [InverseProperty(nameof(Stack.DependentOn))]
    [DeleteBehavior(DeleteBehavior.Restrict)]
    public Stack FromStack { get; set; } = null!;

    /// <summary>The stack waited on. Restrict, for the same reason.</summary>
    [ForeignKey(nameof(ToStackId))]
    [InverseProperty(nameof(Stack.RequiredBy))]
    [DeleteBehavior(DeleteBehavior.Restrict)]
    public Stack ToStack { get; set; } = null!;
}
