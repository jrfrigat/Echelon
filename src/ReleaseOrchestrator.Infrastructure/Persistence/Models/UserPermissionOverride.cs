using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace ReleaseOrchestrator.Infrastructure.Persistence.Models;

/// <summary>Grants a claim to one person, regardless of their groups.</summary>
public class UserPermissionOverride
{
    /// <summary>Primary key.</summary>
    [Key]
    public Guid Id { get; set; }

    /// <summary>The user's stable identifier. 450 characters — the length Identity keys use.</summary>
    [Required, MaxLength(450)]
    public string UserId { get; set; } = string.Empty;

    /// <summary>The claim granted.</summary>
    public Guid PermissionClaimId { get; set; }

    /// <summary>The claim. Cascade: the grant means nothing once the claim is gone.</summary>
    [ForeignKey(nameof(PermissionClaimId))]
    [InverseProperty(nameof(Models.PermissionClaim.UserOverrides))]
    [DeleteBehavior(DeleteBehavior.Cascade)]
    public PermissionClaim PermissionClaim { get; set; } = null!;
}
