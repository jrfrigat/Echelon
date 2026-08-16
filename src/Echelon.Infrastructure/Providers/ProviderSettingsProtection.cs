using System.Text.Json;
using Echelon.Infrastructure.Auth;
using Echelon.Providers.Abstractions;

namespace Echelon.Infrastructure.Providers;

/// <summary>
/// Encrypts the settings a schema marks <see cref="ProviderSettingSchema.Secret"/> before they are
/// stored, and decrypts them before they are used, so a value like a bot token or a second
/// credential is never at rest in plaintext in a settings column -- the same DataProtection
/// treatment a connection access token gets.
/// </summary>
/// <remarks>
/// <para>
/// The ciphertext is DataProtection's, base64-encoded so it can live as a string inside the settings
/// JSON map. A value the schema does not mark secret is stored and read verbatim.
/// </para>
/// <para>
/// This was <c>ActionSecretProtection</c>, under <c>Infrastructure/Actions</c>. Nothing about it was
/// ever specific to action handlers -- it takes a <see cref="ProviderSettingSchema"/> list and a
/// dictionary -- and connections now store provider settings the same way, so a name saying
/// "actions" would have sent the next reader looking for a second implementation that does not
/// exist.
/// </para>
/// </remarks>
public static class ProviderSettingsProtection
{
    /// <summary>Serialises the settings for storage, encrypting the ones the schema marks secret.</summary>
    /// <param name="settings">The settings to store, or null.</param>
    /// <param name="schema">The settings schema, which names the secret keys.</param>
    /// <param name="protector">The DataProtection wrapper.</param>
    public static string? ProtectForStorage(
        IReadOnlyDictionary<string, string>? settings,
        IReadOnlyList<ProviderSettingSchema> schema,
        TokenProtector protector)
    {
        ArgumentNullException.ThrowIfNull(protector);
        if (settings is null) return null;

        var secretKeys = SecretKeys(schema);
        var stored = settings.ToDictionary(
            kv => kv.Key,
            kv => secretKeys.Contains(kv.Key) && !string.IsNullOrEmpty(kv.Value)
                ? Convert.ToBase64String(protector.Protect(kv.Value))
                : kv.Value,
            StringComparer.Ordinal);
        return JsonSerializer.Serialize(stored);
    }

    /// <summary>Reads stored settings, decrypting the ones the schema marks secret.</summary>
    /// <param name="json">The stored settings column, or null.</param>
    /// <param name="schema">The settings schema, which names the secret keys.</param>
    /// <param name="protector">The DataProtection wrapper.</param>
    public static IReadOnlyDictionary<string, string> UnprotectForUse(
        string? json,
        IReadOnlyList<ProviderSettingSchema> schema,
        TokenProtector protector)
    {
        ArgumentNullException.ThrowIfNull(protector);

        var stored = string.IsNullOrWhiteSpace(json)
            ? new Dictionary<string, string>()
            : JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new Dictionary<string, string>();

        var secretKeys = SecretKeys(schema);
        return stored.ToDictionary(
            kv => kv.Key,
            kv => secretKeys.Contains(kv.Key) && !string.IsNullOrEmpty(kv.Value)
                ? protector.Unprotect(Convert.FromBase64String(kv.Value))
                : kv.Value,
            StringComparer.Ordinal);
    }

    private static HashSet<string> SecretKeys(IReadOnlyList<ProviderSettingSchema> schema) =>
        schema.Where(s => s.Secret).Select(s => s.Key).ToHashSet(StringComparer.Ordinal);
}
