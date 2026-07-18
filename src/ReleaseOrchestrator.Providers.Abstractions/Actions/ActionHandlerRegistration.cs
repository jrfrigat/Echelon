namespace ReleaseOrchestrator.Providers.Abstractions.Actions;

/// <summary>
/// Declares that an action handler is registered under <paramref name="ActionType"/>.
/// </summary>
/// <remarks>
/// Same reason as <see cref="VcsProviderRegistration"/> and <see cref="Deploy.DeployStrategyRegistration"/>:
/// keyed DI cannot enumerate its keys, so this is what lets the factory list the available action
/// types and the API validate a binding's action type before storing it.
/// </remarks>
/// <param name="ActionType">The canonical key, as produced by <see cref="ProviderKey.Normalize"/>.</param>
public sealed record ActionHandlerRegistration(string ActionType);
