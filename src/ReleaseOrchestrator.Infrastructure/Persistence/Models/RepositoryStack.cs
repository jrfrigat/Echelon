using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace ReleaseOrchestrator.Infrastructure.Persistence.Models;

/// <summary>Membership of a repository in a stack. The pair is the key — a repository joins a stack once.</summary>
[PrimaryKey(nameof(RepositoryId), nameof(StackId))]
public class RepositoryStack
{
    /// <summary>The repository.</summary>
    public Guid RepositoryId { get; set; }

    /// <summary>The stack it belongs to.</summary>
    public Guid StackId { get; set; }

    /// <summary>The repository. Cascade by convention: membership means nothing without it.</summary>
    [ForeignKey(nameof(RepositoryId))]
    [InverseProperty(nameof(Models.Repository.RepositoryStacks))]
    public Repository Repository { get; set; } = null!;

    /// <summary>The stack. Cascade by convention: membership means nothing without it.</summary>
    [ForeignKey(nameof(StackId))]
    [InverseProperty(nameof(Models.Stack.RepositoryStacks))]
    public Stack Stack { get; set; } = null!;
}
