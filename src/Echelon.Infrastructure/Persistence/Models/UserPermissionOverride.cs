using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Echelon.Infrastructure.Persistence.Models;

/// <summary>Grants a claim to one person, regardless of their groups.</summary>
/// <remarks>See <see cref="GroupPermissionMapping"/> - the same race, with the same consequence.</remarks>
[Index(nameof(UserId), nameof(PermissionClaimId), IsUnique = true, Name = "IX_UserPermissionOverride_UserId_PermissionClaimId")]
public class UserPermissionOverride
{
    /// <summary>Primary key.</summary>
    [Key]
    public Guid Id { get; set; }

    /// <summary>
    /// The Entra ID object id, normalised to a "D"-format GUID - exactly 36 characters, because
    /// <c>UserIdentifier.TryNormalize</c> is the only way a value reaches this column and it parses
    /// a GUID or rejects the input.
    /// </summary>
    /// <remarks>
    /// It was 450, the length Identity uses for its own keys, on a column that cannot hold anything
    /// but a GUID. That is not merely generous: nvarchar(450) is 900 bytes, SQL Server's entire
    /// key-length budget for an index, so the unique index above could not have been created
    /// alongside a 16-byte Guid. The invariant the code already enforces makes the constraint
    /// expressible.
    /// </remarks>
    [Required, MaxLength(36)]
    public string UserId { get; set; } = string.Empty;

    /// <summary>The claim granted.</summary>
    public Guid PermissionClaimId { get; set; }

    /// <summary>The claim. Cascade: the grant means nothing once the claim is gone.</summary>
    [ForeignKey(nameof(PermissionClaimId))]
    [InverseProperty(nameof(Models.PermissionClaim.UserOverrides))]
    [DeleteBehavior(DeleteBehavior.Cascade)]
    public PermissionClaim PermissionClaim { get; set; } = null!;
}
