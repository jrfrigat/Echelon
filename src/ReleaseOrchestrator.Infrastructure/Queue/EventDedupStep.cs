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
/// <see cref="IHasEventIdentity"/> it first checks the inbox for (Source, EventId): a match is a
/// duplicate of an event already handled to completion, so it short-circuits and Rebus acks it
/// without invoking the handler. Otherwise the handler runs, and ONLY once it succeeds is the event
/// recorded as processed. Messages with no identity pass straight through, so this is inert for the
/// records that do not carry one.
///
/// <para>
/// Mark-after-handle is the load-bearing ordering: a handler that throws to be retried -- a status
/// event whose opened event has not landed yet -- is never recorded, so Rebus can redeliver it.
/// Recording before handling (the earlier design) turned that retry into a dropped, lost event.
/// The trade is at-least-once delivery, which every ingestion handler is written to tolerate.
/// </para>
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

            if (await inbox.IsProcessedAsync(identity.Source, identity.EventId, HandlerCancellation.Token))
            {
                logger.LogDebug("Duplicate event {Source}/{EventId} dropped", identity.Source, identity.EventId);
                return; // already handled to completion; ack and drop without re-running the handler
            }

            // A throw here propagates so Rebus retries -- and the event stays unrecorded, so the
            // retry is not mistaken for a duplicate.
            await next();
            await inbox.MarkProcessedAsync(identity.Source, identity.EventId, HandlerCancellation.Token);
            return;
        }

        await next();
    }
}
