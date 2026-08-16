namespace ReleaseOrchestrator.Providers.Abstractions.Tracker;

/// <summary>
/// A stored tracker connection, as the factory needs to read it.
/// </summary>
/// <param name="Name">Operator-facing name. Appears in the errors this connection causes.</param>
/// <param name="ProviderType">Which adapter serves it; matched in canonical form.</param>
/// <param name="ApiUrl">Base address of the tracker's API.</param>
/// <param name="EncryptedAccessToken">Opaque here - only the factory can decrypt it.</param>
/// <param name="ProviderSettingsJson">Settings only this connection's adapter understands, or null.</param>
/// <remarks>See <see cref="Vcs.VcsConnectionDescriptor"/> - the same shape, for the same reason.</remarks>
public record TrackerConnectionDescriptor(
    string Name,
    string ProviderType,
    string ApiUrl,
    byte[] EncryptedAccessToken,
    string? ProviderSettingsJson);
