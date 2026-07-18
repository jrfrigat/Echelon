namespace ReleaseOrchestrator.Providers.Abstractions.Actions;

/// <summary>
/// What an action handler needs to run: the event that fired it, the binding's settings, and the
/// event's payload.
/// </summary>
/// <remarks>
/// Payload is a flat string map (task key, environment, status, ...) so a handler and the dispatcher
/// share no domain types -- the same reason the deploy and ingestion ports stay thin
/// (docs/issues/005-extension-model.md). Provider credentials, when a handler needs them (a tracker
/// mutation), are resolved by the handler from its own factory, not carried here.
/// </remarks>
/// <param name="EventType">The event that triggered the action, e.g. <c>RolloutSucceeded</c>.</param>
/// <param name="Settings">The binding's schema-declared settings (a Telegram chat id, a target status, ...).</param>
/// <param name="Payload">The event's data as a flat string map.</param>
public sealed record ActionContext(
    string EventType,
    IReadOnlyDictionary<string, string> Settings,
    IReadOnlyDictionary<string, string> Payload);
