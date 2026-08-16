using ReleaseOrchestrator.Application.DTOs;

namespace ReleaseOrchestrator.Application.Services;

/// <summary>
/// Builds and reads per-task rollout plans: the projection of the atlas rooted at one target task.
/// </summary>
/// <remarks>
/// The sole planner since the pivot retired the global release plan: a plan is always rooted at one
/// target task and covers its dependency closure.
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
    /// A readable, diffable artifact of what the plan is - for review, for attaching to a change
    /// record, and for seeing the tree without clicking through it. It is also the input format of
    /// <see cref="ImportPlanAsync"/>: what round-trips is the wave assignment, since everything else
    /// in the document belongs to the atlas.
    /// </remarks>
    Task<string?> ExportPlanYamlAsync(Guid taskId, CancellationToken ct = default);

    /// <summary>
    /// Checks a plan document against the task's derived plan without storing anything.
    /// </summary>
    /// <param name="taskId">The target task.</param>
    /// <param name="document">The plan, in the export schema.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <remarks>
    /// The same work <see cref="ImportPlanAsync"/> does, stopped before the write - deliberately the
    /// same code, not a parallel check. A validate that agreed with import only by convention would be
    /// worse than none: it would eventually pass a document import then rejects, or accept one import
    /// silently reorders.
    /// </remarks>
    Task<PlanImportDto> ValidatePlanAsync(Guid taskId, string document, CancellationToken ct = default);

    /// <summary>
    /// Applies a plan document: the wave assignment becomes ordering deltas, and the plan is rebuilt
    /// from them.
    /// </summary>
    /// <param name="taskId">The target task.</param>
    /// <param name="document">The plan, in the export schema.</param>
    /// <param name="force">
    /// Accept a document whose order breaks a task dependency or hard repository link. It skips the
    /// REFUSAL only - the plan still records every constraint it breaks.
    /// </param>
    /// <param name="actor">Who imported it.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <remarks>
    /// Nothing about the imported plan is stored as a plan: plan rows are rebuilt on every ingestion
    /// event and would carry the import away with them. What is stored is the deltas, which every
    /// later rebuild replays - the same mechanism an operator's drag-and-drop uses.
    /// </remarks>
    Task<PlanImportDto> ImportPlanAsync(
        Guid taskId, string document, bool force, ActorRef? actor, CancellationToken ct = default);

    /// <summary>
    /// Rebuilds the target task's plan from the atlas and makes it the active plan for that task.
    /// </summary>
    /// <param name="taskId">The target task.</param>
    /// <param name="actor">
    /// Who asked for the rebuild, or <c>null</c> when the planner ran itself - the recalculation
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
