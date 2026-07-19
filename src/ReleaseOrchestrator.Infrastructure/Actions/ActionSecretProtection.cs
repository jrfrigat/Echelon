using System.Text.Json;
using ReleaseOrchestrator.Infrastructure.Auth;
using ReleaseOrchestrator.Providers.Abstractions;

namespace ReleaseOrchestrator.Infrastructure.Actions;

/// <summary>
/// Encrypts the settings a handler marks <see cref="ProviderSettingSchema.Secret"/> before they are
/// stored, and decrypts them before the handler runs, so a value like a Telegram bot token is never
/// at rest in plaintext in <c>ActionBinding.SettingsJson</c> -- the same DataProtection treatment a
/// connection access token gets.
/// </summary>
/// <remarks>
/// The ciphertext is DataProtection's, base64-encoded so it can live as a string inside the settings
/// JSON map. A value the handler's schema does not mark secret is stored and read verbatim.
/// </remarks>
public static class ActionSecretProtection
{
    /// <summary>Serialises the settings for storage, encrypting the ones the schema marks secret.</summary>
    /// <param name="settings">The settings to store, or null.</param>
    /// <param name="schema">The handler's settings schema, which names the secret keys.</param>
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
    /// <param name="json">The stored <c>SettingsJson</c>, or null.</param>
    /// <param name="schema">The handler's settings schema, which names the secret keys.</param>
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
