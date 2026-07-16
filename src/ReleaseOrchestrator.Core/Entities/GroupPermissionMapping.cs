namespace ReleaseOrchestrator.Core.Entities;

public class GroupPermissionMapping
{
    public Guid Id { get; set; }
    public string AdGroupSid { get; set; } = string.Empty;
    public Guid PermissionClaimId { get; set; }

    public PermissionClaim PermissionClaim { get; set; } = null!;
}
