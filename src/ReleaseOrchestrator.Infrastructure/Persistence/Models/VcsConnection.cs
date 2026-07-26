using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;
using ReleaseOrchestrator.Providers.Abstractions;

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
    /// Settings that only this connection's adapter understands, as a JSON object; null when it
    /// needs none.
    /// </summary>
    /// <remarks>
    /// The counterpart of <see cref="TrackerConnection.ProviderSettingsJson"/>, and added for the
    /// same reason one step later: without it a VCS adapter had nowhere to put a setting, so the
    /// only way to give one provider a field was a column every provider would carry. Every
    /// key here is declared by the adapter's <c>SettingsSchema</c> and validated against it before
    /// storage; nothing outside the adapter reads the contents.
    /// </remarks>
    [MaxLength(ProviderSettingsBag.MaxJsonLength)]
    public string? ProviderSettingsJson { get; set; }

    // There is no ready-for-deploy label column any more: a single label promoting a merge request to
    // a "deployable" status was replaced by per-environment readiness rules over signals (label,
    // mr-status, pipeline). See ReadinessRule and DeploymentEnvironment.ReadinessRuleId.

    // How events arrive (push vs poll) is no longer a column: it is a property of the provider TYPE
    // (gitlab-webhook vs gitlab-poll), and the poll interval a poll type needs lives in the provider
    // settings bag under VcsPollSettings.IntervalKey. See VcsProviderRegistration.Ingestion.

    /// <summary>Repositories served by this connection.</summary>
    [InverseProperty(nameof(Repository.Connection))]
    public ICollection<Repository> Repositories { get; set; } = [];
}
