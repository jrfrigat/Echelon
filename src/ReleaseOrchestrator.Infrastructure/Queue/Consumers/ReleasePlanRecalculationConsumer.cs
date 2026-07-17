using Microsoft.Extensions.Logging;
using Rebus.Handlers;
using ReleaseOrchestrator.Application.Contracts.Messages;
using ReleaseOrchestrator.Application.Services;

namespace ReleaseOrchestrator.Infrastructure.Queue.Consumers;

/// <summary>
/// Rebuilds the release plan, coalescing bursts.
///
/// The work runs inside the consumer, so the broker only acknowledges the message once the
/// plan exists. The previous design handed the request to an in-process debounce timer and
/// acknowledged immediately: a restart inside the 15-second window lost the recalculation
/// outright, and each replica ran its own timer over the same data.
///
/// Coalescing no longer needs a timer. A burst of N events becomes N messages: the first
/// rebuilds, and the rest find a plan whose snapshot already covers them, costing one query.
/// </summary>
public class ReleasePlanRecalculationConsumer(
    IReleasePlannerService planner,
    ILogger<ReleasePlanRecalculationConsumer> logger) : IHandleMessages<ReleasePlanRecalculationRequested>
{
    /// <inheritdoc/>
    public async Task Handle(ReleasePlanRecalculationRequested message)
    {
        var ct = HandlerCancellation.Token;

        if (await planner.IsPlanCurrentAsync(message.RequestedAt, ct))
        {
            logger.LogDebug("Skipping recalculation for {Reason}: the current plan already covers it.", message.Reason);
            return;
        }

        await planner.RecalculateAsync(ct);
        logger.LogInformation("Release plan recalculated. Reason: {Reason}", message.Reason);
    }
}
