namespace ReleaseOrchestrator.Providers.Abstractions.Actions;

/// <summary>Resolves the action handler a binding names.</summary>
/// <remarks>
/// The single place that maps an action-type key to an <see cref="IActionHandler"/>, hiding the
/// keyed lookup exactly as the VCS and deploy-strategy factories do -- the key is a database column.
/// </remarks>
public interface IActionHandlerFactory
{
    /// <summary>The registered action-type keys, canonical form, for validation and error messages.</summary>
    IReadOnlyCollection<string> AvailableActionTypes { get; }

    /// <summary>The settings the named action type accepts.</summary>
    /// <param name="actionType">Action type; matched in canonical form.</param>
    /// <exception cref="UnknownProviderException">No handler is registered for that type.</exception>
    IReadOnlyList<ProviderSettingSchema> GetSettingsSchema(string actionType);

    /// <summary>Resolves the handler for an action type.</summary>
    /// <param name="actionType">Action type; matched in canonical form.</param>
    /// <exception cref="UnknownProviderException">No handler is registered for that type; the message lists those that are.</exception>
    IActionHandler Resolve(string actionType);
}
