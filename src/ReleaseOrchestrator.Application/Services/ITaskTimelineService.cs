using ReleaseOrchestrator.Application.DTOs;

namespace ReleaseOrchestrator.Application.Services;

/// <summary>
/// Assembles the history of one task: when it arrived, what happened to its merge requests, how its
/// plan changed, and who launched it where.
/// </summary>
/// <remarks>
/// <para>
/// A read model computed from the rows the system already writes, not a log kept alongside them.
/// That is the whole design: there is one copy of each fact, so the history cannot drift from the
/// state it describes, and it answers for tasks that existed long before this feature did.
/// </para>
/// <para>
/// The cost is that it can only report what the schema happens to hold. Where a fact was never
/// recorded - a plan's author before authorship existed, a task's arrival before arrival was
/// stamped - the answer says so through <see cref="TimelineCoverageDto"/> rather than rendering an
/// absence that reads as "nothing happened".
/// </para>
/// </remarks>
public interface ITaskTimelineService
{
    /// <summary>
    /// Builds the timeline for a task, newest first.
    /// </summary>
    /// <param name="taskId">The task.</param>
    /// <param name="limit">Maximum entries per source before truncation is reported.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// The timeline, or <c>null</c> when no such task exists in either the live database or the
    /// archive. An archived task answers with its history rather than with null: "archived" and
    /// "never existed" are different, and a caller cannot tell them apart from a 404.
    /// </returns>
    Task<TaskTimelineDto?> GetAsync(Guid taskId, int limit = 200, CancellationToken ct = default);
}
