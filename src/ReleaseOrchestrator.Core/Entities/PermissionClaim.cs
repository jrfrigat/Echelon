namespace ReleaseOrchestrator.Core.Entities;

public class PermissionClaim
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;

    public ICollection<GroupPermissionMapping> GroupMappings { get; set; } = [];
    public ICollection<UserPermissionOverride> UserOverrides { get; set; } = [];
}
