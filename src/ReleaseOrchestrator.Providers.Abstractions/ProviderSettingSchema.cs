namespace ReleaseOrchestrator.Providers.Abstractions;

/// <summary>
/// The kind of value a provider setting holds, so a UI renders the right control and the API
/// validates the input rather than storing anything and failing on first use.
/// </summary>
/// <remarks>
/// Deliberately a short, closed set. It grew from "everything is a string" once real settings
/// needed a number (a poll interval), a choice (which field a task key is read from) and a pattern
/// (the regex that reads it) — three things a plain text box cannot check before storage. It is not
/// a general type system: a new kind is added only when a provider actually needs one.
/// </remarks>
public enum ProviderSettingKind
{
    /// <summary>A free-text string. The default, and what every setting was before kinds existed.</summary>
    Text = 0,

    /// <summary>An integer, optionally bounded by <see cref="ProviderSettingSchema.Min"/> and <see cref="ProviderSettingSchema.Max"/>.</summary>
    Int = 1,

    /// <summary>One of a fixed set of values, listed in <see cref="ProviderSettingSchema.Options"/>.</summary>
    Enum = 2,

    /// <summary>A .NET regular expression, validated by whether it compiles.</summary>
    Regex = 3
}

/// <summary>
/// One provider-specific setting a connection may carry, described well enough for a UI to render
/// a field for it and for the API to validate it.
/// </summary>
/// <remarks>
/// This exists so that provider vocabulary stops leaking upward. Yandex.Tracker needs an
/// organization id; GitLab needs nothing. Before this, that single fact was spelled out in the
/// entity, the API contract and the admin form — so "add a provider" meant editing all three, and
/// the one provider that needed a setting decided the shape for every provider that did not.
///
/// Still deliberately narrow — a key, a label, a kind and a few constraints — not a general schema
/// language. The <see cref="Kind"/> exists because storing a number or a pattern as an unchecked
/// string means the mistake surfaces on first use, far from the form where it was made; validating
/// against the declared kind reports it at the point of entry instead.
/// </remarks>
/// <param name="Key">Key inside the connection's settings bag. Stable — it is persisted.</param>
/// <param name="Label">Short human label for the field. Rendered as-is.</param>
/// <param name="Description">What the value is and where to find it. Shown as a hint.</param>
/// <param name="Required">
/// When true, the connection cannot be saved without it, and the adapter is entitled to assume
/// it is present rather than degrade quietly.
/// </param>
/// <param name="Secret">
/// When true, the value is write-only: never returned by the API, and masked in the UI. Orthogonal
/// to <see cref="Kind"/> — a secret is always a masked text field regardless of kind.
/// </param>
/// <param name="Kind">How the value is typed and rendered; <see cref="ProviderSettingKind.Text"/> by default.</param>
/// <param name="Options">
/// The allowed values, for <see cref="ProviderSettingKind.Enum"/>. Ignored for other kinds. A value
/// outside this set is rejected.
/// </param>
/// <param name="Default">
/// The value a UI pre-fills for a fresh connection. Advisory: the adapter still applies its own
/// default when reading, so an operator who clears an optional field does not break it.
/// </param>
/// <param name="Min">Inclusive lower bound for <see cref="ProviderSettingKind.Int"/>; null for no bound.</param>
/// <param name="Max">Inclusive upper bound for <see cref="ProviderSettingKind.Int"/>; null for no bound.</param>
public record ProviderSettingSchema(
    string Key,
    string Label,
    string? Description = null,
    bool Required = false,
    bool Secret = false,
    ProviderSettingKind Kind = ProviderSettingKind.Text,
    IReadOnlyList<string>? Options = null,
    string? Default = null,
    int? Min = null,
    int? Max = null);
