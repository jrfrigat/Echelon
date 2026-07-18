using Microsoft.Extensions.DependencyInjection;
using ReleaseOrchestrator.Providers.Abstractions;
using ReleaseOrchestrator.Providers.Abstractions.Deploy;

namespace ReleaseOrchestrator.Infrastructure.Providers;

/// <summary>
/// Resolves the deploy strategy a repository names, by its key.
/// </summary>
/// <remarks>
/// The keyed lookup is hidden here for the same reason as <see cref="VcsProviderFactory"/>: the key
/// is <c>Repository.DeployStrategyKey</c>, a database column not known until a row is read, so
/// <c>[FromKeyedServices]</c> cannot express it and a <c>GetRequiredKeyedService</c> behind a factory
/// is the shape that fits. Unlike the VCS factory it binds no credentials -- a strategy is stateless
/// and takes a <see cref="DeployContext"/> per call, which the executor builds with the decrypted token.
/// </remarks>
internal sealed class DeployStrategyFactory(
    IServiceProvider services,
    IEnumerable<DeployStrategyRegistration> registrations) : IDeployStrategyFactory
{
    private readonly IReadOnlyCollection<string> _available =
        registrations.Select(r => ProviderKey.Normalize(r.Key)).Distinct(StringComparer.Ordinal).ToList();

    /// <inheritdoc/>
    public IReadOnlyCollection<string> AvailableStrategies => _available;

    /// <inheritdoc/>
    public IReadOnlyList<ProviderSettingSchema> GetSettingsSchema(string strategyKey)
    {
        var key = ProviderKey.Normalize(strategyKey);
        if (!_available.Contains(key))
            throw new UnknownProviderException("Deploy strategy", strategyKey, _available);

        return services.GetRequiredKeyedService<IDeployStrategy>(key).SettingsSchema;
    }

    /// <inheritdoc/>
    public IDeployStrategy Resolve(string strategyKey)
    {
        var key = ProviderKey.Normalize(strategyKey);
        if (!_available.Contains(key))
            throw new UnknownProviderException("Deploy strategy", strategyKey, _available);

        return services.GetRequiredKeyedService<IDeployStrategy>(key);
    }
}
