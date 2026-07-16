using ReleaseOrchestrator.Core.Enums;

namespace ReleaseOrchestrator.Core.Entities;

public class VcsConnection
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public VcsType VcsType { get; set; }
    public string ApiUrl { get; set; } = string.Empty;
    public byte[] EncryptedAccessToken { get; set; } = [];

    /// <summary>
    /// VCS label that marks a merge request as deployable (README §5). When an opened MR
    /// carries it, the MR enters the release plan. Null disables label-driven promotion
    /// for this connection, leaving only the manual API.
    /// </summary>
    public string? ReadyForDeployLabel { get; set; } = DefaultReadyForDeployLabel;

    public const string DefaultReadyForDeployLabel = "ready-for-deploy";

    public ICollection<Repository> Repositories { get; set; } = [];
}
