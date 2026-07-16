namespace ReleaseOrchestrator.Providers.Abstractions;

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
/// Deliberately narrow: a key, a label, and whether it is required or secret. Not a general
/// schema language. Providers here are written by this team, and the settings they need are a
/// handful of strings; anything richer would be a type system nobody asked for.
/// </remarks>
/// <param name="Key">Key inside the connection's settings bag. Stable — it is persisted.</param>
/// <param name="Label">Short human label for the field. Rendered as-is.</param>
/// <param name="Description">What the value is and where to find it. Shown as a hint.</param>
/// <param name="Required">
/// When true, the connection cannot be saved without it, and the adapter is entitled to assume
/// it is present rather than degrade quietly.
/// </param>
/// <param name="Secret">
/// When true, the value is write-only: never returned by the API, and masked in the UI. No
/// provider needs this yet — tokens have their own encrypted column — but a provider requiring a
/// second credential should not have to invent the concept under deadline.
/// </param>
public record ProviderSettingSchema(
    string Key,
    string Label,
    string? Description = null,
    bool Required = false,
    bool Secret = false);
