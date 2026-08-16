namespace ReleaseOrchestrator.Providers.Abstractions.Vcs;

/// <summary>
/// A stored VCS connection, as the factory needs to read it.
/// </summary>
/// <param name="Name">Operator-facing name. Appears in the errors this connection causes.</param>
/// <param name="ProviderType">Which adapter serves it; matched in canonical form.</param>
/// <param name="ApiUrl">Base address of the provider's API.</param>
/// <param name="EncryptedAccessToken">Opaque here - only the factory can decrypt it.</param>
/// <remarks>
/// The factory used to take the <c>VcsConnection</c> entity, which put a persistence type in the
/// provider contract: an adapter package could not be referenced without also referencing the
/// database model. This says what the factory actually reads, and nothing about how it is stored.
/// </remarks>
public record VcsConnectionDescriptor(
    string Name,
    string ProviderType,
    string ApiUrl,
    byte[] EncryptedAccessToken);
