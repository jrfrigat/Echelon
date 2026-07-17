using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace ReleaseOrchestrator.Infrastructure.Persistence.Models;

/// <summary>Grants a claim to everyone in an AD group.</summary>
/// <remarks>
/// The pair is unique, and the index is the guarantee rather than the controller's
/// check-then-insert, which races with itself: two admins granting the same claim at once both
/// see nothing and both insert. Revocation then deletes one row by id, logs "revoked", and the
/// group keeps the permission — a permission that survives its own revocation, reported as gone.
/// </remarks>
[Index(nameof(AdGroupSid), nameof(PermissionClaimId), IsUnique = true, Name = "IX_GroupPermissionMapping_AdGroupSid_PermissionClaimId")]
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
