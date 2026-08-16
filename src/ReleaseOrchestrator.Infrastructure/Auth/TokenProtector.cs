using Microsoft.AspNetCore.DataProtection;

namespace ReleaseOrchestrator.Infrastructure.Auth;

/// <summary>
/// Encrypts and decrypts the provider credentials stored on connections.
/// </summary>
/// <remarks>
/// <para>
/// One named purpose, <c>ConnectionTokens.v1</c>, shared by every caller - a token encrypted under
/// one purpose cannot be decrypted under another, so changing the string here makes every stored
/// credential unreadable. The <c>.v1</c> suffix exists so a future rotation can be a new purpose
/// alongside this one rather than a silent break.
/// </para>
/// <para>
/// The keys themselves come from the Data Protection setup, which must be shared across replicas
/// and must survive a restart. With the default in-memory, per-process key ring, a token written by
/// one replica is undecryptable by every other and by the same one after a restart.
/// </para>
/// </remarks>
public class TokenProtector(IDataProtectionProvider provider)
{
    private readonly IDataProtector _protector = provider.CreateProtector("ConnectionTokens.v1");

    /// <summary>Encrypts a credential for storage.</summary>
    /// <param name="plaintext">The credential as the operator supplied it.</param>
    /// <returns>Ciphertext to store. Never log or return this to a caller.</returns>
    public byte[] Protect(string plaintext) =>
        _protector.Protect(System.Text.Encoding.UTF8.GetBytes(plaintext));

    /// <summary>Decrypts a stored credential for immediate use.</summary>
    /// <param name="ciphertext">What <see cref="Protect"/> produced.</param>
    /// <returns>The credential.</returns>
    /// <exception cref="System.Security.Cryptography.CryptographicException">
    /// The payload was written under a different purpose or a key ring this process cannot read -
    /// in practice, a key ring that was not persisted or not shared between replicas.
    /// </exception>
    public string Unprotect(byte[] ciphertext) =>
        System.Text.Encoding.UTF8.GetString(_protector.Unprotect(ciphertext));
}
