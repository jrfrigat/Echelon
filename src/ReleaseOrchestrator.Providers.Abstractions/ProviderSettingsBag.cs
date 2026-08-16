using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ReleaseOrchestrator.Providers.Abstractions;

/// <summary>Why a submitted settings bag was rejected.</summary>
public enum ProviderSettingsError
{
    /// <summary>Accepted.</summary>
    None = 0,

    /// <summary>
    /// A key the provider never declared. Rejected rather than ignored.
    /// </summary>
    /// <remarks>
    /// A misspelled key stored silently does nothing, and the operator then sees the adapter
    /// complain that a setting is missing while looking straight at a form where they filled it in.
    /// Naming the unknown key is the only report that points at the actual mistake.
    /// </remarks>
    UnknownKey = 1,

    /// <summary>A setting the provider declared <see cref="ProviderSettingSchema.Required"/> is absent or blank.</summary>
    MissingRequired = 2,

    /// <summary>The bag does not fit the column it is stored in.</summary>
    TooLong = 3,

    /// <summary>
    /// A value does not match the kind its setting declares - not an integer, not one of the
    /// allowed options, or not a compilable pattern.
    /// </summary>
    /// <remarks>
    /// Caught here, at entry, rather than left to storage: a poll interval of "soon" or a task-key
    /// pattern that does not compile would otherwise be stored happily and fail only when the poller
    /// or the linker first used it, far from the form that set it.
    /// </remarks>
    InvalidValue = 4
}

/// <summary>
/// Validates a submitted settings bag against the schema its provider declared, and renders it for
/// storage.
/// </summary>
/// <remarks>
/// <para>
/// Pure, and deliberately in this project rather than in the API: the rules are the provider
/// contract's, not HTTP's, and both connection controllers plus anything that imports a connection
/// must apply exactly the same ones. It carries no localization for the same reason - it returns
/// which rule failed and which key failed it, and the caller words that for its own audience.
/// </para>
/// <para>
/// Note what this does <b>not</b> do: it never encrypts. Values a schema marks
/// <see cref="ProviderSettingSchema.Secret"/> are protected one layer out, where the data-protection
/// stack lives; this project has no access to it and should not.
/// </para>
/// </remarks>
public static class ProviderSettingsBag
{
    /// <summary>
    /// Longest settings JSON that can be stored, matching the column the entities declare.
    /// </summary>
    /// <remarks>
    /// Checked here rather than left to the database, because the database's answer is either a
    /// silent truncation or a provider-specific exception surfacing as a 500 - neither of which
    /// tells an operator that one field was too long.
    /// </remarks>
    public const int MaxJsonLength = 4000;

    /// <summary>
    /// Checks <paramref name="submitted"/> against <paramref name="schema"/> and normalizes it.
    /// </summary>
    /// <param name="submitted">What the caller sent; null is the same as empty.</param>
    /// <param name="schema">The settings the provider declares. An empty schema accepts only an empty bag.</param>
    /// <param name="normalized">
    /// On success, the accepted settings: trimmed, with blanks dropped. Blank and absent collapse to
    /// one representation so that "cleared" and "never set" cannot drift apart in storage.
    /// </param>
    /// <param name="offendingKey">On failure, the key at fault; null when the failure is not about one key.</param>
    /// <returns>The rule that failed, or <see cref="ProviderSettingsError.None"/>.</returns>
    public static ProviderSettingsError Validate(
        IReadOnlyDictionary<string, string?>? submitted,
        IReadOnlyList<ProviderSettingSchema> schema,
        out IReadOnlyDictionary<string, string> normalized,
        out string? offendingKey)
    {
        ArgumentNullException.ThrowIfNull(schema);

        normalized = new Dictionary<string, string>(StringComparer.Ordinal);
        offendingKey = null;

        var declared = schema.Select(s => s.Key).ToHashSet(StringComparer.Ordinal);
        var accepted = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var (key, value) in submitted ?? new Dictionary<string, string?>())
        {
            if (!declared.Contains(key))
            {
                offendingKey = key;
                return ProviderSettingsError.UnknownKey;
            }

            if (!string.IsNullOrWhiteSpace(value))
                accepted[key] = value.Trim();
        }

        foreach (var setting in schema.Where(s => s.Required))
        {
            if (accepted.ContainsKey(setting.Key)) continue;

            offendingKey = setting.Key;
            return ProviderSettingsError.MissingRequired;
        }

        // Typed validation runs only over accepted (present, non-blank) values: a blank optional
        // value was already dropped, and a blank required one already failed above.
        foreach (var setting in schema)
        {
            if (!accepted.TryGetValue(setting.Key, out var value)) continue;
            if (IsValidForKind(setting, value)) continue;

            offendingKey = setting.Key;
            return ProviderSettingsError.InvalidValue;
        }

        if (Serialize(accepted)?.Length > MaxJsonLength)
            return ProviderSettingsError.TooLong;

        normalized = accepted;
        return ProviderSettingsError.None;
    }

    /// <summary>True when <paramref name="value"/> satisfies the kind and bounds <paramref name="setting"/> declares.</summary>
    private static bool IsValidForKind(ProviderSettingSchema setting, string value) =>
        setting.Kind switch
        {
            ProviderSettingKind.Int => IsValidInt(setting, value),
            // Ordinal: an option is a stable identifier, not display text, so casing is significant.
            ProviderSettingKind.Enum => setting.Options is { } options && options.Contains(value, StringComparer.Ordinal),
            ProviderSettingKind.Regex => IsCompilableRegex(value),
            _ => true
        };

    private static bool IsValidInt(ProviderSettingSchema setting, string value)
    {
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
            return false;

        if (setting.Min is { } min && n < min) return false;
        if (setting.Max is { } max && n > max) return false;

        return true;
    }

    private static bool IsCompilableRegex(string value)
    {
        try
        {
            // Construction is the compile; the timeout guards a pathological pattern from hanging
            // validation, not the stored value's own matching (that carries its own timeout).
            _ = new Regex(value, RegexOptions.None, TimeSpan.FromMilliseconds(100));
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>Renders a validated bag for storage; null when there is nothing to store.</summary>
    /// <param name="settings">The accepted settings.</param>
    /// <remarks>
    /// An empty bag stores as null rather than <c>{}</c>, so that a connection needing no settings
    /// reads the same whether it was saved before or after its provider declared any.
    /// </remarks>
    public static string? Serialize(IReadOnlyDictionary<string, string> settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return settings.Count == 0 ? null : JsonSerializer.Serialize(settings);
    }

    /// <summary>Reads a stored bag back, treating unparsable JSON as empty.</summary>
    /// <param name="json">The stored column, or null.</param>
    /// <remarks>
    /// Unparsable is not thrown on, because the callers are listings: refusing to render a
    /// connection because one of its settings is malformed hides the very row an operator needs to
    /// open in order to fix it. Whatever actually uses the connection fails loudly instead.
    /// </remarks>
    public static IReadOnlyDictionary<string, string> Deserialize(string? json)
    {
        if (!TryDeserialize(json, out var settings)) return new Dictionary<string, string>(StringComparer.Ordinal);

        return settings;
    }

    /// <summary>Reads a stored bag back, reporting whether it could be parsed at all.</summary>
    /// <param name="json">The stored column, or null.</param>
    /// <param name="settings">The parsed settings; empty when this returns false.</param>
    /// <returns>False when the column held something that is not a JSON string map.</returns>
    public static bool TryDeserialize(
        string? json,
        [NotNullWhen(true)] out IReadOnlyDictionary<string, string>? settings)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            settings = new Dictionary<string, string>(StringComparer.Ordinal);
            return true;
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
            settings = parsed is null
                ? new Dictionary<string, string>(StringComparer.Ordinal)
                : new Dictionary<string, string>(parsed, StringComparer.Ordinal);
            return true;
        }
        catch (JsonException)
        {
            settings = null;
            return false;
        }
    }
}
