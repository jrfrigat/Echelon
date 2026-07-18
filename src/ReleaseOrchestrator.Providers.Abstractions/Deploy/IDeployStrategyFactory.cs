namespace ReleaseOrchestrator.Providers.Abstractions.Deploy;

/// <summary>Resolves the deploy strategy a repository names.</summary>
/// <remarks>
/// The single place that maps a strategy key to an <see cref="IDeployStrategy"/>, hiding the keyed
/// lookup exactly as the VCS provider factory does -- the key is a database column, so it is not
/// known until a row is read.
/// </remarks>
public interface IDeployStrategyFactory
{
    /// <summary>The registered strategy keys, canonical form, for validation and error messages.</summary>
    IReadOnlyCollection<string> AvailableStrategies { get; }

    /// <summary>The settings the named strategy accepts, so a UI can render a form for it.</summary>
    /// <param name="strategyKey">Strategy key; matched in canonical form.</param>
    /// <exception cref="UnknownProviderException">No strategy is registered for that key.</exception>
    IReadOnlyList<ProviderSettingSchema> GetSettingsSchema(string strategyKey);

    /// <summary>Resolves the strategy for a key.</summary>
    /// <param name="strategyKey">Strategy key; matched in canonical form.</param>
    /// <exception cref="UnknownProviderException">No strategy is registered for that key; the message lists those that are.</exception>
    IDeployStrategy Resolve(string strategyKey);
}
