using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;
using ReleaseOrchestrator.Core.Enums;

namespace ReleaseOrchestrator.Infrastructure.Persistence.Models;

/// <summary>A configured connection to a version control system.</summary>
[Index(nameof(Name), IsUnique = true, Name = "UQ_VcsConnection_Name")]
public class VcsConnection
{
    /// <summary>Primary key.</summary>
    [Key]
    public Guid Id { get; set; }

    /// <summary>Operator-facing name; unique, and what webhook routes and messages refer to.</summary>
    [Required, MaxLength(200)]
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
    [Required, MaxLength(100)]
    public string ProviderType { get; set; } = string.Empty;

    /// <summary>Base address of the provider's API.</summary>
    [Required, MaxLength(500)]
    public string ApiUrl { get; set; } = string.Empty;

    /// <summary>The access token, encrypted at rest by the data-protection stack.</summary>
    public byte[] EncryptedAccessToken { get; set; } = [];

    /// <summary>
    /// VCS label that marks a merge request as deployable (README §5). When an opened MR
    /// carries it, the MR enters the release plan. Null disables label-driven promotion
    /// for this connection, leaving only the manual API.
    /// </summary>
    [MaxLength(200)]
    public string? ReadyForDeployLabel { get; set; } = DefaultReadyForDeployLabel;

    /// <summary>The label assumed when a connection does not name one.</summary>
    public const string DefaultReadyForDeployLabel = "ready-for-deploy";

    /// <summary>
    /// How merge-request events reach the service: pushed by webhook (the default), or polled by the
    /// service for a setup that cannot send webhooks.
    /// </summary>
    public IngestionMode IngestionMode { get; set; } = IngestionMode.Push;

    /// <summary>How often the poller lists this connection's merge requests, in seconds (poll mode only).</summary>
    public int PollIntervalSeconds { get; set; } = 300;

    /// <summary>Repositories served by this connection.</summary>
    [InverseProperty(nameof(Repository.Connection))]
    public ICollection<Repository> Repositories { get; set; } = [];
}
