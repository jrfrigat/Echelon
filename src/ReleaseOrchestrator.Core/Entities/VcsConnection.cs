namespace ReleaseOrchestrator.Core.Entities;

/// <summary>A configured connection to a version control system.</summary>
public class VcsConnection
{
    /// <summary>Primary key.</summary>
    public Guid Id { get; set; }

    /// <summary>Operator-facing name; unique, and what webhook routes and messages refer to.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Which adapter serves this connection, e.g. <c>gitlab</c>.
    /// </summary>
    /// <remarks>
    /// A string, not an enum. As an enum, every new provider was a change to the domain plus a
    /// data migration — for a value the domain never branches on, since the type is meaningful
    /// only to the factory that resolves an adapter for it. Compared through
    /// <c>ProviderKey.Normalize</c>, never with <c>==</c> against a literal.
    /// </remarks>
    public string ProviderType { get; set; } = string.Empty;

    /// <summary>Base address of the provider's API.</summary>
    public string ApiUrl { get; set; } = string.Empty;

    /// <summary>The access token, encrypted at rest by the data-protection stack.</summary>
    public byte[] EncryptedAccessToken { get; set; } = [];

    /// <summary>
    /// VCS label that marks a merge request as deployable (README §5). When an opened MR
    /// carries it, the MR enters the release plan. Null disables label-driven promotion
    /// for this connection, leaving only the manual API.
    /// </summary>
    public string? ReadyForDeployLabel { get; set; } = DefaultReadyForDeployLabel;

    /// <summary>The label assumed when a connection does not name one.</summary>
    public const string DefaultReadyForDeployLabel = "ready-for-deploy";

    /// <summary>Repositories served by this connection.</summary>
    public ICollection<Repository> Repositories { get; set; } = [];
}
