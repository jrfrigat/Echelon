namespace ReleaseOrchestrator.Application.ReleasePlanning;

/// <summary>
/// Cuts the transitive dependency closure of one target task out of the task-dependency graph.
/// </summary>
/// <remarks>
/// Pure: no EF, no I/O, no clock. The closure is the target plus every task it depends on, directly
/// or transitively -- the tasks whose merge requests a rollout of the target must deploy before the
/// target's own. This is the per-task counterpart to the global plan: the global graph (the atlas)
/// holds every edge, and a rollout plan is the projection rooted at one task (see
/// docs/issues/006-per-task-planning.md).
///
/// Cycle-safe by construction: a task already in the closure is never expanded again, so a
/// dependency cycle yields a finite closure rather than looping. Whether that cycle is a fatal
/// ordering problem is the planner's concern -- <see cref="ReleasePlanGraph"/> breaks cycles and
/// records the conflict -- not the closure's.
/// </remarks>
public static class PlanClosureBuilder
{
    /// <summary>
    /// Returns the target task together with all of its transitive prerequisites.
    /// </summary>
    /// <param name="targetTaskId">The task an operator wants to roll out.</param>
    /// <param name="dependsOn">
    /// Task-dependency adjacency: for each task, the tasks it deploys after (its prerequisites). A
    /// task absent from the map is treated as having no prerequisites, so callers may pass only the
    /// tasks that actually declare dependencies.
    /// </param>
    /// <returns>
    /// The closure, in ascending id order so the result is deterministic regardless of traversal.
    /// Always contains <paramref name="targetTaskId"/>.
    /// </returns>
    public static IReadOnlyList<Guid> Closure(
        Guid targetTaskId,
        IReadOnlyDictionary<Guid, IReadOnlyList<Guid>> dependsOn)
    {
        var closure = new HashSet<Guid>();
        var pending = new Stack<Guid>();
        pending.Push(targetTaskId);

        while (pending.Count > 0)
        {
            var task = pending.Pop();

            // Adding returns false when the task is already in the closure. Skipping it there is what
            // makes a dependency cycle terminate: every task is expanded at most once.
            if (!closure.Add(task)) continue;

            if (dependsOn.TryGetValue(task, out var prerequisites))
                foreach (var prerequisite in prerequisites)
                    if (!closure.Contains(prerequisite))
                        pending.Push(prerequisite);
        }

        // Membership is a set; the sort exists only so the same input always yields the same order,
        // independent of the traversal that produced it.
        var ordered = closure.ToList();
        ordered.Sort();
        return ordered;
    }
}
