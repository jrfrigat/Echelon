using Microsoft.Extensions.DependencyInjection;
using ReleaseOrchestrator.Providers.Abstractions.Actions;

namespace ReleaseOrchestrator.Infrastructure.Actions;

/// <summary>Registers the action-handler factory and the built-in handlers.</summary>
/// <remarks>
/// Each handler is keyed by its action type and paired with an <see cref="ActionHandlerRegistration"/>
/// so the factory can enumerate and validate the keys -- the same shape as the VCS and deploy
/// registrations. A third-party handler adds one more class plus one more registration here.
/// </remarks>
public static class ActionHandlerExtensions
{
    private static readonly TimeSpan HttpTimeout = TimeSpan.FromSeconds(15);

    /// <summary>Adds the action-handler factory and the Telegram and tracker handlers.</summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The same collection, for chaining.</returns>
    public static IServiceCollection AddActionHandlers(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<IActionHandlerFactory, ActionHandlerFactory>();
        services.AddScoped<ActionDispatcher>();

        services.AddHttpClient<TelegramActionHandler>(c => c.Timeout = HttpTimeout);
        services.AddKeyedScoped<IActionHandler>(TelegramActionHandler.ActionType, (sp, _) => sp.GetRequiredService<TelegramActionHandler>());
        services.AddSingleton(new ActionHandlerRegistration(TelegramActionHandler.ActionType,
            "Sends a Telegram message when a rollout event fires."));

        services.AddScoped<TrackerStatusActionHandler>();
        services.AddKeyedScoped<IActionHandler>(TrackerStatusActionHandler.ActionType, (sp, _) => sp.GetRequiredService<TrackerStatusActionHandler>());
        services.AddSingleton(new ActionHandlerRegistration(TrackerStatusActionHandler.ActionType,
            "Moves the tracker issue to a status when a rollout event fires."));

        services.AddScoped<TrackerCommentActionHandler>();
        services.AddKeyedScoped<IActionHandler>(TrackerCommentActionHandler.ActionType, (sp, _) => sp.GetRequiredService<TrackerCommentActionHandler>());
        services.AddSingleton(new ActionHandlerRegistration(TrackerCommentActionHandler.ActionType,
            "Posts a comment to the tracker issue when a rollout event fires."));

        return services;
    }
}
