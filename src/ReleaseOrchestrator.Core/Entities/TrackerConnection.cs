using ReleaseOrchestrator.Core.Enums;

namespace ReleaseOrchestrator.Core.Entities;

public class TrackerConnection
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public TrackerType TrackerType { get; set; }
    public string ApiUrl { get; set; } = string.Empty;
    public byte[] EncryptedAccessToken { get; set; } = [];
    public string? OrgId { get; set; }

    public ICollection<TaskItem> Tasks { get; set; } = [];
    public ICollection<Repository> Repositories { get; set; } = [];
}
