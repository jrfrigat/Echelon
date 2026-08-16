using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;
using ReleaseOrchestrator.Providers.Abstractions;

namespace ReleaseOrchestrator.Infrastructure.Persistence.Models;

/// <summary>A configured connection to an issue tracker.</summary>
/// <remarks>
/// Name is uniquely indexed, like <see cref="VcsConnection"/>: the controllers' check-then-insert is
/// only advisory, and consumers resolve a connection by Name via FirstOrDefault, so two rows sharing
/// a Name would let task sync, status updates and tracker actions run against the wrong connection.
/// </remarks>
[Index(nameof(Name), IsUnique = true, Name = "UQ_TrackerConnection_Name")]
public class TrackerConnection
{
    /// <summary>Primary key.</summary>
    [Key]
    public Guid Id { get; set; }

    /// <summary>Operator-facing name, and what webhook routes and messages refer to.</summary>
    [Required, MaxLength(200)]
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Which adapter serves this connection, e.g. <c>yandextracker-webhook</c>.
    /// </summary>
    /// <remarks>See <see cref="VcsConnection.ProviderType"/> for why this is a string.</remarks>
    [Required, MaxLength(100)]
    public string ProviderType { get; set; } = string.Empty;

    /// <summary>Base address of the tracker's API.</summary>
    [Required, MaxLength(500)]
    public string ApiUrl { get; set; } = string.Empty;

    /// <summary>The access token, encrypted at rest by the data-protection stack.</summary>
    public byte[] EncryptedAccessToken { get; set; } = [];

    /// <summary>
    /// Settings that only this connection's adapter understands, as a JSON object; null when it
    /// needs none.
    /// </summary>
    /// <remarks>
    /// This column used to be <c>OrgId</c> - an organization identifier that only Yandex.Tracker
    /// has. A named column for one vendor's concept means the domain describes that vendor rather
    /// than "a tracker", and the next provider needing its own field would have added a second
    /// such column. The adapter parses this into its own typed options and validates what it
    /// requires; nothing outside the adapter reads the contents.
    /// </remarks>
    [MaxLength(ProviderSettingsBag.MaxJsonLength)]
    public string? ProviderSettingsJson { get; set; }

    /// <summary>Tasks imported from this tracker.</summary>
    [InverseProperty(nameof(TaskItem.TrackerConnection))]
    public ICollection<TaskItem> Tasks { get; set; } = [];

    /// <summary>Repositories whose issue keys are resolved against this tracker.</summary>
    [InverseProperty(nameof(Repository.TrackerConnection))]
    public ICollection<Repository> Repositories { get; set; } = [];
}
