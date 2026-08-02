using ReleaseOrchestrator.Core.Enums;

namespace ReleaseOrchestrator.Application.ReleasePlanning;

/// <summary>
/// Turns an ordering rule document into the ordering edges the planner already understands.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately NOT a second planner. The output is edges of the same kinds
/// (<see cref="PlanEdgeKind.RepoHard"/> / <see cref="PlanEdgeKind.RepoSoft"/>) that
/// <see cref="ReleasePlanGraph"/> already derives, breaks cycles among and reports conflicts for. The
/// invariant docs/issues/006 states — import, hand edit and recalculate must all derive edges with
/// one piece of code — is exactly what a rule language with its own topological sort would break:
/// the preview would show one order and the run would use another.
/// </para>
/// <para>
/// Pure: no EF, no clock. The caller supplies the candidates; this decides only what waits for what.
/// </para>
/// </remarks>
public static class OrderingRuleCompiler
{
    /// <summary>
    /// Expands the document's group ordering into "deploy A before B" pairs over the given work.
    /// </summary>
    /// <param name="rules">The document.</param>
    /// <param name="candidates">The work in the plan being ordered.</param>
    /// <returns>
    /// Edges as (from, to, type): <c>from</c> deploys first. Deduplicated, and deterministic in the
    /// order of <paramref name="candidates"/> — the planner needs the same input to give the same plan.
    /// </returns>
    public static IReadOnlyList<OrderingEdge> Compile(
        OrderingRules rules, IReadOnlyList<OrderingCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(candidates);

        var matched = rules.Groups.ToDictionary(
            g => g.Key,
            g => candidates.Where(g.Value.Matches).ToList(),
            StringComparer.Ordinal);

        var edges = new List<OrderingEdge>();
        var seen = new HashSet<(Guid, Guid)>();

        foreach (var rule in rules.Order)
        {
            if (!matched.TryGetValue(rule.Group, out var waiters)) continue;

            foreach (var neededName in rule.Needs)
            {
                if (!matched.TryGetValue(neededName, out var needed)) continue;

                foreach (var waiter in waiters)
                    foreach (var first in needed)
                    {
                        // A self-edge carries no ordering. It happens whenever the two groups overlap,
                        // which is ordinary — "partner/*" and "everything on this connector" can both
                        // match the same merge request.
                        if (waiter.MergeRequestId == first.MergeRequestId) continue;

                        // WithinTask is what keeps unrelated tasks parallel: applied across the plan,
                        // "frontend after backend" chains one task's frontend behind another task's
                        // backend. Correct, and quietly slower than the operator asked for.
                        if (rule.Scope == OrderScope.WithinTask
                            && (waiter.TaskId is null || waiter.TaskId != first.TaskId)) continue;

                        if (seen.Add((first.MergeRequestId, waiter.MergeRequestId)))
                            edges.Add(new OrderingEdge(first.MergeRequestId, waiter.MergeRequestId, rule.Type));
                    }
            }
        }

        return edges;
    }

    /// <summary>
    /// The effective wait policy for one task: the stored default, then the document, then the first
    /// matching per-task override.
    /// </summary>
    /// <param name="rules">The document.</param>
    /// <param name="stored">The installation default the document may leave alone.</param>
    /// <param name="task">A candidate belonging to the task, for the override selectors to read.</param>
    /// <remarks>
    /// First match wins rather than last, and rather than merging every match: a reader resolves the
    /// answer by scanning down and stopping, which is the only resolution order that stays obvious as
    /// the list grows.
    /// </remarks>
    public static TaskWaitPolicy ResolvePolicy(
        OrderingRules rules, TaskWaitPolicy stored, OrderingCandidate? task)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(stored);

        var policy = stored.OverriddenBy(
            rules.Tasks.WaitForSubtasks, rules.Tasks.WaitForLinked, rules.Tasks.GroupOrder);

        if (task is null) return policy;

        var match = rules.Tasks.Overrides.FirstOrDefault(o => o.Match.Matches(task));

        return match is null
            ? policy
            : policy.OverriddenBy(match.WaitForSubtasks, match.WaitForLinked, match.GroupOrder);
    }
}

/// <summary>"Deploy <paramref name="From"/> before <paramref name="To"/>", and how firmly.</summary>
/// <param name="From">The merge request that goes first.</param>
/// <param name="To">The merge request that waits.</param>
/// <param name="Type">Hard never yields to break a cycle; soft yields first.</param>
public sealed record OrderingEdge(Guid From, Guid To, StackDependencyType Type);
