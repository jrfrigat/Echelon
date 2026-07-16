using MassTransit;
using ReleaseOrchestrator.Application.Contracts.Messages;

namespace ReleaseOrchestrator.Infrastructure.Queue.Consumers;

// Routes the recalculation request through DebouncedRecalculationService
// to prevent burst recalculations when many events arrive simultaneously.
public class ReleasePlanRecalculationConsumer(DebouncedRecalculationService debounce) : IConsumer<ReleasePlanRecalculationRequested>
{
    public Task Consume(ConsumeContext<ReleasePlanRecalculationRequested> context)
    {
        debounce.RequestRecalculation(context.Message.Reason ?? "queue event");
        return Task.CompletedTask;
    }
}
