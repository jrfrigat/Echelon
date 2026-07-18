using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Rebus.Messages;
using Rebus.Pipeline;
using ReleaseOrchestrator.Application.Contracts.Messages;

namespace ReleaseOrchestrator.Infrastructure.Queue;

/// <summary>
/// Drops an ingestion event that has already been processed, before it reaches a handler.
/// </summary>
/// <remarks>
/// Runs in the incoming pipeline just before dispatch. For a message carrying an
/// <see cref="IHasEventIdentity"/> it records the (Source, EventId) in the inbox; a duplicate
/// short-circuits the pipeline, so Rebus acks and drops it without invoking the handler. Messages
/// with no identity pass straight through, so this is inert until the ingestion records adopt the
/// interface (docs/issues/008-ingestion-and-messaging.md).
///
/// It resolves a scope from the application's root provider per message rather than reaching for
/// Rebus's ambient scope, which keeps it independent of the container-integration internals.
/// </remarks>
internal sealed class EventDedupStep(IServiceProvider rootProvider, ILogger<EventDedupStep> logger) : IIncomingStep
{
    /// <inheritdoc/>
    public async Task Process(IncomingStepContext context, Func<Task> next)
    {
        var message = context.Load<Message>();
        if (message?.Body is IHasEventIdentity identity && !string.IsNullOrWhiteSpace(identity.EventId))
        {
            await using var scope = rootProvider.CreateAsyncScope();
            var inbox = scope.ServiceProvider.GetRequiredService<ProcessedEventInbox>();

            if (!await inbox.TryMarkProcessedAsync(identity.Source, identity.EventId, HandlerCancellation.Token))
            {
                logger.LogDebug("Duplicate event {Source}/{EventId} dropped", identity.Source, identity.EventId);
                return; // short-circuit: the message is acked and dropped, the handler never runs
            }
        }

        await next();
    }
}
