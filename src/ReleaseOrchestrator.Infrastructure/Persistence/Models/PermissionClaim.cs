using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace ReleaseOrchestrator.Infrastructure.Persistence.Models;

/// <summary>A permission an operator can hold, e.g. <c>release.plan.approve</c>.</summary>
/// <remarks>Claims, not roles: a role is a bundle whose contents have to be guessed.</remarks>
[Index(nameof(Name), IsUnique = true)]
public class PermissionClaim
{
    /// <summary>Primary key.</summary>
    [Key]
    public Guid Id { get; set; }

    /// <summary>The claim name; unique. Matches the policy the API authorises against.</summary>
    [Required, MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    /// <summary>AD groups granted this claim.</summary>
    [InverseProperty(nameof(GroupPermissionMapping.PermissionClaim))]
    public ICollection<GroupPermissionMapping> GroupMappings { get; set; } = [];

    /// <summary>Individuals granted this claim directly.</summary>
    [InverseProperty(nameof(UserPermissionOverride.PermissionClaim))]
    public ICollection<UserPermissionOverride> UserOverrides { get; set; } = [];
}
