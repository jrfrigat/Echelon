using Echelon.Core.Enums;

namespace Echelon.Application.ReleasePlanning;

/// <summary>
/// One task's prerequisites, as the two sources state them before any policy is applied.
/// </summary>
/// <param name="TaskId">The task that waits.</param>
/// <param name="Subtasks">Tasks beneath it in the hierarchy.</param>
/// <param name="Linked">Tasks it declares a dependency on.</param>
/// <param name="ManualOrder">
/// An operator's explicit sequence over this task's prerequisites, or empty when none was set. Only
/// the prerequisites the policy admits are ordered; an entry naming anything else is ignored.
/// </param>
public sealed record TaskPrerequisites(
    Guid TaskId,
    IReadOnlyList<Guid> Subtasks,
    IReadOnlyList<Guid> Linked,
    IReadOnlyList<Guid> ManualOrder);

/// <summary>
/// Turns each task's declared prerequisites into the adjacency the planner walks, honouring the
/// policy that says which kinds count and in what order.
/// </summary>
/// <remarks>
/// Pure: no EF, no I/O, no clock. This is the seam where "what the tracker said" becomes "what the
/// rollout waits for", and it is the whole reason the policy is testable - the merge used to happen
/// inside an EF query, where the only way to check it was to run one.
/// </remarks>
public static class TaskWaitGraph
{
    /// <summary>
    /// Builds the "tasks this one deploys after" map.
    /// </summary>
    /// <param name="tasks">Each task's prerequisites from both sources.</param>
    /// <param name="policyFor">
    /// The effective policy for a task - the global default with that task's overrides applied.
    /// </param>
    /// <returns>
    /// Adjacency in the shape <see cref="PlanClosureBuilder.Closure"/> and the planner expect. A task
    /// with no admitted prerequisites is omitted rather than mapped to an empty list, which is the
    /// same thing to every reader and keeps the map to what actually constrains anything.
    /// </returns>
    public static Dictionary<Guid, IReadOnlyList<Guid>> Build(
        IEnumerable<TaskPrerequisites> tasks,
        Func<Guid, TaskWaitPolicy> policyFor)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        ArgumentNullException.ThrowIfNull(policyFor);

        var adjacency = new Dictionary<Guid, IReadOnlyList<Guid>>();

        foreach (var task in tasks)
        {
            var policy = policyFor(task.TaskId);

            var subtasks = policy.WaitForSubtasks ? task.Subtasks : [];
            var linked = policy.WaitForLinked ? task.Linked : [];

            // Distinct: a tracker can state a dependency the hierarchy already implies, and the same
            // prerequisite twice is the same edge twice.
            var prerequisites = subtasks.Concat(linked)
                .Where(id => id != task.TaskId)   // a self-prerequisite carries no ordering
                .Distinct()
                .ToList();

            if (prerequisites.Count > 0)
                adjacency[task.TaskId] = prerequisites;

            AddGroupOrderEdges(adjacency, policy.GroupOrder, subtasks, linked, task.TaskId);
            AddManualOrderEdges(adjacency, task.ManualOrder, prerequisites);
        }

        return adjacency;
    }

    /// <summary>
    /// Makes one whole group of prerequisites deploy before the other.
    /// </summary>
    /// <remarks>
    /// These edges are between tasks that may be entirely unrelated - that is what the operator asked
    /// for, and it is also why this can turn an acyclic graph cyclic. The planner breaks cycles and
    /// records the conflict, so the failure mode is a reported dropped edge rather than a hang.
    /// Self-edges are skipped: a task in both groups cannot precede itself.
    /// </remarks>
    private static void AddGroupOrderEdges(
        Dictionary<Guid, IReadOnlyList<Guid>> adjacency,
        PrerequisiteGroupOrder order,
        IReadOnlyList<Guid> subtasks,
        IReadOnlyList<Guid> linked,
        Guid owner)
    {
        var (first, second) = order switch
        {
            PrerequisiteGroupOrder.SubtasksFirst => (subtasks, linked),
            PrerequisiteGroupOrder.LinkedFirst => (linked, subtasks),
            _ => ([], [])
        };

        if (first.Count == 0 || second.Count == 0) return;

        foreach (var waiter in second)
            foreach (var waitedOn in first)
                if (waiter != waitedOn && waiter != owner)
                    Append(adjacency, waiter, waitedOn);
    }

    /// <summary>
    /// Chains the operator's explicit sequence: each named prerequisite deploys after the one before it.
    /// </summary>
    /// <remarks>
    /// A chain, not a full ordering: consecutive pairs are enough, because "after" is transitive
    /// through the graph. Entries the policy did not admit are dropped rather than honoured - the
    /// sequence is a preference over the prerequisites that exist, and a stale entry naming a task
    /// that is no longer one must not resurrect it into the closure.
    /// </remarks>
    private static void AddManualOrderEdges(
        Dictionary<Guid, IReadOnlyList<Guid>> adjacency,
        IReadOnlyList<Guid> manualOrder,
        IReadOnlyList<Guid> admitted)
    {
        if (manualOrder.Count < 2) return;

        var sequence = manualOrder.Where(admitted.Contains).Distinct().ToList();

        for (var i = 1; i < sequence.Count; i++)
            Append(adjacency, sequence[i], sequence[i - 1]);
    }

    private static void Append(Dictionary<Guid, IReadOnlyList<Guid>> adjacency, Guid task, Guid dependsOn)
    {
        if (adjacency.TryGetValue(task, out var existing))
        {
            if (existing.Contains(dependsOn)) return;
            adjacency[task] = [.. existing, dependsOn];
            return;
        }

        adjacency[task] = [dependsOn];
    }
}
