using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Rebus.Handlers;
using ReleaseOrchestrator.Application.Contracts.Messages;
using ReleaseOrchestrator.Application.Services;
using ReleaseOrchestrator.Infrastructure.Persistence;

namespace ReleaseOrchestrator.Infrastructure.Queue.Consumers;

/// <summary>
/// Rebuilds every active per-task rollout plan when an ingestion change may have altered a closure.
/// </summary>
/// <remarks>
/// A merge request opening or changing status, or a task edit, can change the deploy order of any
/// task whose plan is currently live. Which tasks are affected is not cheap to know precisely - a
/// merge request reaches a task through its branch key, a task edit through the dependency graph -
/// so every active plan is rebuilt. There are only as many active plans as there are tasks with a
/// built rollout plan, and a rebuild that finds nothing changed is a couple of queries.
///
/// <para>
/// The work runs inside the handler, so the broker acknowledges only once the plans are rebuilt: a
/// failure faults the message and Rebus redelivers it, rather than dropping the recalculation. This
/// is the point-to-point successor to the old global-plan recalculation the pivot retired - the
/// ingestion consumers still publish one <see cref="ReleasePlanRecalculationRequested"/> per event.
/// </para>
/// <para>
/// A burst of N events becomes N messages, each rebuilding the same small set. Coalescing per-task
/// recalculation (the global planner had it) is a possible optimisation, not a correctness need:
/// each rebuild is idempotent, replacing the active plan with an equal one when nothing changed.
/// </para>
/// </remarks>
public class ReleasePlanRecalculationConsumer(
    AppDbContext db,
    IRolloutPlannerService planner,
    ILogger<ReleasePlanRecalculationConsumer> logger) : IHandleMessages<ReleasePlanRecalculationRequested>
{
    /// <inheritdoc/>
    public async Task Handle(ReleasePlanRecalculationRequested message)
    {
        var ct = HandlerCancellation.Token;

        var targetTaskIds = await db.RolloutPlans
            .Where(p => p.IsActive)
            .Select(p => p.TargetTaskId)
            .ToListAsync(ct);

        // Null actor: this is the planner running itself in response to an ingestion event, not
        // anyone asking. Passing the operator whose action fanned out to here would be worse than
        // passing nothing -- causality is not authorship, and one MR label can rebuild every plan
        // in the system.
        foreach (var taskId in targetTaskIds)
            await planner.RecalculateAsync(taskId, actor: null, ct);

        if (targetTaskIds.Count > 0)
            logger.LogInformation(
                "Recalculated {Count} active plan(s). Reason: {Reason}", targetTaskIds.Count, message.Reason);
    }
}
