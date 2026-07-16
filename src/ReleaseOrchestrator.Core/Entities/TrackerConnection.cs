namespace ReleaseOrchestrator.Core.Entities;

/// <summary>A configured connection to an issue tracker.</summary>
public class TrackerConnection
{
    /// <summary>Primary key.</summary>
    public Guid Id { get; set; }

    /// <summary>Operator-facing name; unique, and what webhook routes and messages refer to.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Which adapter serves this connection, e.g. <c>yandextracker</c>.
    /// </summary>
    /// <remarks>See <see cref="VcsConnection.ProviderType"/> for why this is a string.</remarks>
    public string ProviderType { get; set; } = string.Empty;

    /// <summary>Base address of the tracker's API.</summary>
    public string ApiUrl { get; set; } = string.Empty;

    /// <summary>The access token, encrypted at rest by the data-protection stack.</summary>
    public byte[] EncryptedAccessToken { get; set; } = [];

    /// <summary>
    /// Settings that only this connection's adapter understands, as a JSON object; null when it
    /// needs none.
    /// </summary>
    /// <remarks>
    /// This column used to be <c>OrgId</c> — an organization identifier that only Yandex.Tracker
    /// has. A named column for one vendor's concept means the domain describes that vendor rather
    /// than "a tracker", and the next provider needing its own field would have added a second
    /// such column. The adapter parses this into its own typed options and validates what it
    /// requires; nothing outside the adapter reads the contents.
    /// </remarks>
    public string? ProviderSettingsJson { get; set; }

    /// <summary>Tasks imported from this tracker.</summary>
    public ICollection<TaskItem> Tasks { get; set; } = [];

    /// <summary>Repositories whose issue keys are resolved against this tracker.</summary>
    public ICollection<Repository> Repositories { get; set; } = [];
}
