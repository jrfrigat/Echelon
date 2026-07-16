namespace ReleaseOrchestrator.Core.Entities;

public class UserPermissionOverride
{
    public Guid Id { get; set; }
    public string UserId { get; set; } = string.Empty;
    public Guid PermissionClaimId { get; set; }

    public PermissionClaim PermissionClaim { get; set; } = null!;
}
