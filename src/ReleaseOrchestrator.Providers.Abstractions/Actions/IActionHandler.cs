namespace ReleaseOrchestrator.Providers.Abstractions.Actions;

/// <summary>
/// A side effect to run when an event fires: change a tracker status, add a comment, send a Telegram
/// message, or something custom.
/// </summary>
/// <remarks>
/// The third pluggable family (Telegram is neither a VCS nor a tracker), selected by an action-type
/// key on an <c>ActionBinding</c>. A binding is data; a new action TYPE is one class plus a
/// registration. Actions run off the deploy path and their failures never fail a rollout step
///.
/// </remarks>
public interface IActionHandler
{
    /// <summary>The settings this action accepts, so a UI can render a form and the API can validate.</summary>
    IReadOnlyList<ProviderSettingSchema> SettingsSchema { get; }

    /// <summary>Runs the action.</summary>
    /// <param name="context">The event, the binding's settings, and the event payload.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<ActionResult> ExecuteAsync(ActionContext context, CancellationToken ct);
}
