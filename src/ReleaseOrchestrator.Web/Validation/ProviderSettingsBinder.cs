using Microsoft.Extensions.Localization;
using ReleaseOrchestrator.Infrastructure.Auth;
using ReleaseOrchestrator.Infrastructure.Providers;
using ReleaseOrchestrator.Providers.Abstractions;
using ReleaseOrchestrator.Web.Resources;

namespace ReleaseOrchestrator.Web.Validation;

/// <summary>
/// Turns the settings an operator submitted for a connection into the JSON stored on it, checked
/// against the schema its provider declares and with secret values encrypted.
/// </summary>
/// <remarks>
/// Shared by the VCS and tracker controllers because both hold a bag of provider-declared settings
/// and both must treat it identically. Before this, the tracker controller unwrapped the bag into a
/// literal <c>orgId</c> field — one provider's vocabulary, spelled out in the HTTP contract, so that
/// giving a second provider a second setting meant editing the controller, the request record and
/// the form. Nothing here knows the name of any setting.
/// </remarks>
public static class ProviderSettingsBinder
{
    /// <summary>
    /// Validates and merges submitted settings, returning the JSON to store.
    /// </summary>
    /// <param name="submitted">What the caller sent; null is the same as sending nothing.</param>
    /// <param name="schema">The settings the connection's provider declares.</param>
    /// <param name="existingJson">
    /// The connection's currently stored settings on update, or null when creating. Needed because
    /// a secret that the caller left blank must survive the save.
    /// </param>
    /// <param name="protector">The DataProtection wrapper used to encrypt secret values.</param>
    /// <param name="localizer">Supplies the rejection wording, as with <see cref="ApiUrlValidator"/>.</param>
    /// <param name="settingsJson">On success, the value for the entity's settings column; null when empty.</param>
    /// <param name="error">On failure, the operator-facing reason.</param>
    public static bool TryBind(
        IReadOnlyDictionary<string, string?>? submitted,
        IReadOnlyList<ProviderSettingSchema> schema,
        string? existingJson,
        TokenProtector protector,
        IStringLocalizer<ApiStrings> localizer,
        out string? settingsJson,
        out string error)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(localizer);

        settingsJson = null;
        error = string.Empty;

        var merged = Merge(submitted, schema, existingJson, protector);

        var failure = ProviderSettingsBag.Validate(merged, schema, out var normalized, out var key);
        switch (failure)
        {
            case ProviderSettingsError.UnknownKey:
                error = localizer[
                    "Provider_UnknownSetting", key ?? "?", string.Join(", ", schema.Select(s => s.Key))];
                return false;

            case ProviderSettingsError.MissingRequired:
                error = localizer["Provider_MissingSetting", key ?? "?"];
                return false;

            case ProviderSettingsError.TooLong:
                error = localizer["Provider_SettingsTooLong", ProviderSettingsBag.MaxJsonLength];
                return false;
        }

        settingsJson = ProviderSettingsProtection.ProtectForStorage(normalized, schema, protector);
        return true;
    }

    /// <summary>
    /// Overlays the submitted values onto what is already stored, in plaintext.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Blank means two different things, and the difference is not an inconsistency:
    /// </para>
    /// <list type="bullet">
    /// <item>
    /// For a <b>secret</b> setting, blank means <b>keep</b>. The form cannot show the operator what
    /// is stored, so an empty box is what an untouched field looks like — the same convention the
    /// access token already uses, and treating it as "clear" would silently delete a credential
    /// every time anyone renamed a connection.
    /// </item>
    /// <item>
    /// For every other setting, blank means <b>clear</b>. The form does show the current value, so
    /// an empty box is a deliberate erasure and must be honoured, or a setting could never be
    /// removed once set.
    /// </item>
    /// </list>
    /// <para>
    /// Merging in plaintext, before validation, is what makes a kept secret satisfy a
    /// <see cref="ProviderSettingSchema.Required"/> rule: validating the raw submission instead
    /// would reject every update that did not retype the credential.
    /// </para>
    /// </remarks>
    private static Dictionary<string, string?> Merge(
        IReadOnlyDictionary<string, string?>? submitted,
        IReadOnlyList<ProviderSettingSchema> schema,
        string? existingJson,
        TokenProtector protector)
    {
        var merged = submitted is null
            ? []
            : new Dictionary<string, string?>(submitted, StringComparer.Ordinal);

        var secretKeys = schema.Where(s => s.Secret).Select(s => s.Key).ToHashSet(StringComparer.Ordinal);
        if (secretKeys.Count == 0 || string.IsNullOrWhiteSpace(existingJson)) return merged;

        // Only reached when the provider actually declares a secret, so a connection with none pays
        // nothing for this and never touches the data-protection stack.
        var existing = ProviderSettingsProtection.UnprotectForUse(existingJson, schema, protector);

        foreach (var secretKey in secretKeys)
        {
            var blank = !merged.TryGetValue(secretKey, out var value) || string.IsNullOrWhiteSpace(value);
            if (blank && existing.TryGetValue(secretKey, out var stored) && !string.IsNullOrEmpty(stored))
                merged[secretKey] = stored;
        }

        return merged;
    }

    /// <summary>
    /// Reads a connection's stored settings for display, with secret values withheld.
    /// </summary>
    /// <param name="storedJson">The entity's settings column.</param>
    /// <param name="schema">The settings the connection's provider declares.</param>
    /// <returns>The non-secret settings, keyed as the provider declared them.</returns>
    /// <remarks>
    /// Secret values are dropped rather than masked with a placeholder: a placeholder that reaches
    /// the form would be submitted back as if it were the real value, and the next save would store
    /// the mask. An absent key round-trips correctly through the blank-means-keep rule above.
    /// </remarks>
    public static IReadOnlyDictionary<string, string> ReadForDisplay(
        string? storedJson,
        IReadOnlyList<ProviderSettingSchema> schema)
    {
        ArgumentNullException.ThrowIfNull(schema);

        var secretKeys = schema.Where(s => s.Secret).Select(s => s.Key).ToHashSet(StringComparer.Ordinal);

        // Read raw: the secret values here are still ciphertext, and are about to be discarded, so
        // there is no reason to decrypt them in order to throw them away.
        return ProviderSettingsBag.Deserialize(storedJson)
            .Where(kv => !secretKeys.Contains(kv.Key))
            .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
    }
}
