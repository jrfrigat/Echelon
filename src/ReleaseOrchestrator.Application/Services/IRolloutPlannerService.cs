using ReleaseOrchestrator.Application.DTOs;

namespace ReleaseOrchestrator.Application.Services;

/// <summary>
/// Builds and reads per-task rollout plans: the projection of the atlas rooted at one target task.
/// </summary>
/// <remarks>
/// The sole planner since the pivot retired the global release plan: a plan is always rooted at one
/// target task and covers its dependency closure (docs/issues/009-admin-and-migration.md).
/// </remarks>
public interface IRolloutPlannerService
{
    /// <summary>Lists tasks (a page of them), each with enough to decide whether to roll it out.</summary>
    /// <param name="page">1-based page number.</param>
    /// <param name="pageSize">Page size.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<IReadOnlyList<TaskListItemDto>> ListTasksAsync(int page, int pageSize, CancellationToken ct = default);

    /// <summary>Total number of tasks, for pagination.</summary>
    /// <param name="ct">Cancellation token.</param>
    Task<int> CountTasksAsync(CancellationToken ct = default);

    /// <summary>
    /// One task's own facts and its place in the hierarchy, or <c>null</c> when there is no such task.
    /// Answerable before any plan exists.
    /// </summary>
    /// <param name="taskId">The task.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<TaskDetailDto?> GetTaskAsync(Guid taskId, CancellationToken ct = default);

    /// <summary>The active plan for a task, or <c>null</c> when none has been built yet.</summary>
    /// <param name="taskId">The target task.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<RolloutPlanDto?> GetActivePlanAsync(Guid taskId, CancellationToken ct = default);

    /// <summary>
    /// The task's active plan as a YAML document, or <c>null</c> when it has none.
    /// </summary>
    /// <param name="taskId">The target task.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The plan in the per-task schema of docs/issues/006-per-task-planning.md §6.</returns>
    /// <remarks>
    /// Export only: there is no import yet, so this is a readable, diffable artifact of what the plan
    /// currently is — for review, for attaching to a change record, and for seeing the tree without
    /// clicking through it. It is a projection of the atlas either way, so a round trip would have to
    /// go through the override deltas rather than through this text.
    /// </remarks>
    Task<string?> ExportPlanYamlAsync(Guid taskId, CancellationToken ct = default);

    /// <summary>
    /// Rebuilds the target task's plan from the atlas and makes it the active plan for that task.
    /// </summary>
    /// <param name="taskId">The target task.</param>
    /// <param name="actor">
    /// Who asked for the rebuild, or <c>null</c> when the planner ran itself — the recalculation
    /// consumer rebuilds every active plan on every ingestion event, and that churn has no author.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <remarks>
    /// One method with a nullable actor rather than a second user-aware overload: two entry points
    /// into the same rebuild would eventually differ in what they record, and the one that differed
    /// would be the machine path nobody watches.
    /// </remarks>
    Task<RolloutPlanDto> RecalculateAsync(Guid taskId, ActorRef? actor, CancellationToken ct = default);
}
