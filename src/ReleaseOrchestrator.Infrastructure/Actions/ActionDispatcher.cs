using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ReleaseOrchestrator.Infrastructure.Persistence;
using ReleaseOrchestrator.Providers.Abstractions.Actions;

namespace ReleaseOrchestrator.Infrastructure.Actions;

/// <summary>
/// Runs the action bindings that match an event. Isolated: a handler that throws is logged and the
/// next binding still runs, and nothing here propagates to the caller -- an action must never fail a
/// rollout step (docs/issues/007-execution-engine.md).
/// </summary>
public class ActionDispatcher(AppDbContext db, IActionHandlerFactory factory, ILogger<ActionDispatcher> logger)
{
    /// <summary>Runs every enabled binding for <paramref name="eventType"/> whose scope matches the payload.</summary>
    /// <param name="eventType">The event that fired, e.g. <c>RolloutSucceeded</c>.</param>
    /// <param name="payload">The event data (task, environment, status, trackerConnectionName, ...).</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task DispatchAsync(string eventType, IReadOnlyDictionary<string, string> payload, CancellationToken ct)
    {
        var bindings = await db.ActionBindings
            .Where(b => b.Enabled && b.EventType == eventType)
            .OrderBy(b => b.Order).ThenBy(b => b.Id)
            .AsNoTracking()
            .ToListAsync(ct)
            .ConfigureAwait(false);

        foreach (var binding in bindings)
        {
            if (!ScopeMatches(binding.Scope, payload)) continue;

            try
            {
                var handler = factory.Resolve(binding.ActionType);
                var settings = ParseSettings(binding.SettingsJson);
                var result = await handler.ExecuteAsync(new ActionContext(eventType, settings, payload), ct).ConfigureAwait(false);

                if (!result.Success)
                    logger.LogWarning("Action {ActionType} for event {Event} did not succeed: {Message}",
                        binding.ActionType, eventType, result.Message);
            }
            catch (Exception ex)
            {
                // An action failure is isolated from the deploy path and from the other bindings.
                logger.LogError(ex, "Action {ActionType} for event {Event} threw", binding.ActionType, eventType);
            }
        }
    }

    /// <summary>
    /// A scope like <c>env:prod</c> or <c>task:PROJ-1</c> matches when the payload has that value. An
    /// empty scope matches every event of the type.
    /// </summary>
    private static bool ScopeMatches(string? scope, IReadOnlyDictionary<string, string> payload)
    {
        if (string.IsNullOrWhiteSpace(scope)) return true;

        var separator = scope.IndexOf(':');
        if (separator <= 0) return true; // malformed scope: do not silently exclude

        var key = scope[..separator].Trim();
        var value = scope[(separator + 1)..].Trim();

        var payloadKey = key switch { "env" => "environment", _ => key };
        return payload.TryGetValue(payloadKey, out var actual)
               && string.Equals(actual, value, StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyDictionary<string, string> ParseSettings(string? json) =>
        string.IsNullOrWhiteSpace(json)
            ? new Dictionary<string, string>()
            : JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new Dictionary<string, string>();
}
