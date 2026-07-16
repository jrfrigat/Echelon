using ReleaseOrchestrator.Core.Enums;

namespace ReleaseOrchestrator.Core.Entities;

public class VcsConnection
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public VcsType VcsType { get; set; }
    public string ApiUrl { get; set; } = string.Empty;
    public byte[] EncryptedAccessToken { get; set; } = [];

    public ICollection<Repository> Repositories { get; set; } = [];
}
