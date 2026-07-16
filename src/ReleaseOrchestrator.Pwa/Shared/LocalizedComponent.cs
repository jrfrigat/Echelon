using Microsoft.AspNetCore.Components;
using ReleaseOrchestrator.Pwa.Services;

namespace ReleaseOrchestrator.Pwa.Shared;

/// <summary>
/// Repaints on a language switch.
///
/// Resource strings are read during render, so a component that never re-renders keeps whatever
/// language it was first painted in. Nothing else about a switch marks the tree dirty.
///
/// Derived components must not override <see cref="OnInitialized"/> without calling base, or the
/// subscription never happens; <c>OnInitializedAsync</c> is free.
/// </summary>
public abstract class LocalizedComponent : ComponentBase, IDisposable
{
    /// <summary>The culture the app is rendering in, and the way to change it.</summary>
    [Inject] protected LanguageService Lang { get; set; } = default!;

    /// <inheritdoc />
    protected override void OnInitialized() => Lang.LanguageChanged += OnLanguageChanged;

    private void OnLanguageChanged() => InvokeAsync(StateHasChanged);

    /// <summary>Unsubscribes from the language service, which outlives this component.</summary>
    public virtual void Dispose()
    {
        Lang.LanguageChanged -= OnLanguageChanged;
        GC.SuppressFinalize(this);
    }
}
