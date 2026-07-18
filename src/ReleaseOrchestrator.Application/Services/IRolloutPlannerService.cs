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

    /// <summary>The active plan for a task, or <c>null</c> when none has been built yet.</summary>
    /// <param name="taskId">The target task.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<RolloutPlanDto?> GetActivePlanAsync(Guid taskId, CancellationToken ct = default);

    /// <summary>
    /// Rebuilds the target task's plan from the atlas and makes it the active plan for that task.
    /// </summary>
    /// <param name="taskId">The target task.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<RolloutPlanDto> RecalculateAsync(Guid taskId, CancellationToken ct = default);
}
