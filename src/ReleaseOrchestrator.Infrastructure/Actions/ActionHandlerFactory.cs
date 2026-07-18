using Microsoft.Extensions.DependencyInjection;
using ReleaseOrchestrator.Providers.Abstractions;
using ReleaseOrchestrator.Providers.Abstractions.Actions;

namespace ReleaseOrchestrator.Infrastructure.Actions;

/// <summary>
/// Resolves the action handler a binding names, by its action-type key.
/// </summary>
/// <remarks>
/// The keyed lookup is hidden here for the same reason as the VCS and deploy-strategy factories: the
/// key is a database column not known until a row is read. Handlers are stateless and take an
/// <see cref="ActionContext"/> per call.
/// </remarks>
internal sealed class ActionHandlerFactory(
    IServiceProvider services,
    IEnumerable<ActionHandlerRegistration> registrations) : IActionHandlerFactory
{
    private readonly IReadOnlyCollection<string> _available =
        registrations.Select(r => ProviderKey.Normalize(r.ActionType)).Distinct(StringComparer.Ordinal).ToList();

    /// <inheritdoc/>
    public IReadOnlyCollection<string> AvailableActionTypes => _available;

    /// <inheritdoc/>
    public IReadOnlyList<ProviderSettingSchema> GetSettingsSchema(string actionType)
    {
        var key = ProviderKey.Normalize(actionType);
        if (!_available.Contains(key))
            throw new UnknownProviderException("Action", actionType, _available);

        return services.GetRequiredKeyedService<IActionHandler>(key).SettingsSchema;
    }

    /// <inheritdoc/>
    public IActionHandler Resolve(string actionType)
    {
        var key = ProviderKey.Normalize(actionType);
        if (!_available.Contains(key))
            throw new UnknownProviderException("Action", actionType, _available);

        return services.GetRequiredKeyedService<IActionHandler>(key);
    }
}
