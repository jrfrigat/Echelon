using MassTransit;
using Microsoft.Extensions.Logging;
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
    ILogger<ReleasePlanRecalculationConsumer> logger) : IConsumer<ReleasePlanRecalculationRequested>
{
    public async Task Consume(ConsumeContext<ReleasePlanRecalculationRequested> context)
    {
        var msg = context.Message;

        if (await planner.IsPlanCurrentAsync(msg.RequestedAt, context.CancellationToken))
        {
            logger.LogDebug("Skipping recalculation for {Reason}: the current plan already covers it.", msg.Reason);
            return;
        }

        await planner.RecalculateAsync(context.CancellationToken);
        logger.LogInformation("Release plan recalculated. Reason: {Reason}", msg.Reason);
    }
}
