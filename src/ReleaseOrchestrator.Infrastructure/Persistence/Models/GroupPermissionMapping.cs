using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace ReleaseOrchestrator.Infrastructure.Persistence.Models;

/// <summary>Grants a claim to everyone in an AD group.</summary>
public class GroupPermissionMapping
{
    /// <summary>Primary key.</summary>
    [Key]
    public Guid Id { get; set; }

    /// <summary>The group's SID. A SID, not a name: names are renamed, SIDs are not.</summary>
    [Required, MaxLength(200)]
    public string AdGroupSid { get; set; } = string.Empty;

    /// <summary>The claim granted.</summary>
    public Guid PermissionClaimId { get; set; }

    /// <summary>The claim. Cascade: the grant means nothing once the claim is gone.</summary>
    [ForeignKey(nameof(PermissionClaimId))]
    [InverseProperty(nameof(Models.PermissionClaim.GroupMappings))]
    [DeleteBehavior(DeleteBehavior.Cascade)]
    public PermissionClaim PermissionClaim { get; set; } = null!;
}
