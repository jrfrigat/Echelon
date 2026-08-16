using System.Globalization;
using Microsoft.JSInterop;
using Echelon.Pwa.Resources;

namespace Echelon.Pwa.Services;

/// <summary>
/// Owns the UI culture: resolves it at startup, switches it on demand and remembers the choice.
///
/// Blazor WebAssembly has no request to carry a culture, so nothing sets one for us - the culture
/// is process-wide state this service assigns before the first render. The saved choice lives in
/// localStorage rather than a cookie: an installed PWA has to come back up in the user's language
/// with no server round-trip.
/// </summary>
public sealed class LanguageService(IJSRuntime js)
{
    private const string StorageKey = "blazorCulture";

    /// <summary>The cultures the app ships resources for, in the order the switcher lists them.</summary>
    public static readonly IReadOnlyList<(string Code, string DisplayName)> SupportedCultures =
    [
        ("en", "English"),
        ("ru", "Русский")
    ];

    /// <summary>Two-letter code of the culture currently in effect.</summary>
    public string CurrentCulture { get; private set; } = "en";

    /// <summary>Raised after a switch, so components rendering resource strings can repaint.</summary>
    public event Action? LanguageChanged;

    /// <summary>
    /// Applies the culture the user last chose, else the browser's, else English. Must run before
    /// the first render - switching after one leaves already-painted strings in the old language.
    /// </summary>
    public async Task InitializeCultureAsync()
    {
        var stored = await js.InvokeAsync<string?>("localStorage.getItem", StorageKey);

        // The WASM runtime seeds CurrentUICulture from navigator.language, so the browser's
        // preference needs no JS call of its own.
        Apply(Resolve(stored) ?? Resolve(CultureInfo.CurrentUICulture.Name) ?? "en");
    }

    /// <summary>Switches the UI culture and persists the choice. A no-op if already active.</summary>
    /// <param name="cultureCode">A code from <see cref="SupportedCultures"/>.</param>
    public async Task SetCultureAsync(string cultureCode)
    {
        if (cultureCode == CurrentCulture) return;

        Apply(cultureCode);
        await js.InvokeVoidAsync("localStorage.setItem", StorageKey, cultureCode);

        LanguageChanged?.Invoke();
    }

    private void Apply(string cultureCode)
    {
        CurrentCulture = cultureCode;
        var culture = new CultureInfo(cultureCode);

        // DefaultThreadCurrent* rather than CurrentCulture: WASM resumes continuations on a
        // fresh context, which reads the defaults, so setting only CurrentCulture reverts.
        CultureInfo.DefaultThreadCurrentCulture = culture;
        CultureInfo.DefaultThreadCurrentUICulture = culture;
        UiStrings.Culture = culture;
    }

    /// <summary>Maps a browser/stored code ("ru-RU") onto a supported one ("ru"); null if unsupported.</summary>
    private static string? Resolve(string? code) =>
        string.IsNullOrWhiteSpace(code)
            ? null
            : SupportedCultures
                .Select(c => c.Code)
                .FirstOrDefault(c => code.StartsWith(c, StringComparison.OrdinalIgnoreCase));
}
