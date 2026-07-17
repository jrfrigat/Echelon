using ReleaseOrchestrator.Core.Entities;
using ReleaseOrchestrator.Core.Enums;

namespace ReleaseOrchestrator.Application.ReleasePlanning;

/// <summary>
/// Why one merge request must be deployed before another, and how readily the
/// link may be dropped to break a cycle. Higher value = dropped first
/// (README §5.3: soft stack links yield before task links; hard links never yield).
/// </summary>
public enum PlanEdgeKind
{
    StackHard = 0,
    TaskDependency = 1,
    StackSoft = 2
}

/// <summary>A "deploy <see cref="FromMrId"/> before <see cref="ToMrId"/>" constraint.</summary>
public record PlanEdge(Guid FromMrId, Guid ToMrId, PlanEdgeKind Kind);

/// <summary>A constraint the planner had to drop to produce an ordered plan.</summary>
public record PlanConflict(PlanEdgeKind DroppedEdgeKind, Guid FromMrId, Guid ToMrId, string Reason);

/// <summary>Ordered deploy stages plus every constraint sacrificed to get them.</summary>
public record PlanGraphResult(List<List<Guid>> Stages, List<PlanConflict> Conflicts);

/// <summary>
/// Turns a set of deployable merge requests into ordered stages.
/// Pure: no EF, no I/O, no clock — the whole algorithm is unit-testable as-is.
/// </summary>
public static class ReleasePlanGraph
{
    /// <param name="mrs">
    /// Deployable MRs. Callers must load <c>Task.Dependencies</c> and
    /// <c>Repository.RepositoryStacks.Stack.DependentOn</c>, and pass the list in a
    /// deterministic order — that order decides ties within a stage.
    /// </param>
    public static PlanGraphResult Build(IReadOnlyList<MergeRequest> mrs)
    {
        var ids = mrs.Select(mr => mr.Id).ToHashSet();
        var rank = new Dictionary<Guid, int>(mrs.Count);
        for (int i = 0; i < mrs.Count; i++) rank[mrs[i].Id] = i;

        var edges = BuildEdges(mrs, ids);
        var conflicts = BreakCycles(edges, ids, rank);
        var stages = TopoSort(edges, ids, rank);

        return new PlanGraphResult(stages, conflicts);
    }

    /// <summary>
    /// Constraints an operator may not violate: hard stack links and task dependencies.
    /// Soft stack links are advisory and excluded. Used to vet imported plans (README §6.2)
    /// against the same edge derivation the planner itself uses, so the two cannot drift.
    /// </summary>
    public static IReadOnlyList<PlanEdge> MandatoryEdges(IReadOnlyList<MergeRequest> mrs)
    {
        var ids = mrs.Select(mr => mr.Id).ToHashSet();
        return BuildEdges(mrs, ids)
            .Where(kv => kv.Value != PlanEdgeKind.StackSoft)
            .Select(kv => new PlanEdge(kv.Key.From, kv.Key.To, kv.Value))
            .ToList();
    }

    /// <summary>
    /// Mandatory constraints the given stage assignment does not honour.
    /// </summary>
    /// <param name="mrs">
    /// The plan's merge requests, loaded like <see cref="Build"/> requires.
    /// </param>
    /// <param name="stageOf">Stage sequence per merge request id.</param>
    /// <remarks>
    /// A plan may violate a constraint — an operator sometimes has to deploy against the declared
    /// order — but it may never do so silently, so this is what turns an edit into a recorded
    /// conflict. Soft links are excluded: the planner drops them itself to break cycles, so
    /// reporting them would flag the operator for a choice the planner makes unprompted.
    ///
    /// Comparison is <c>>=</c>, not <c>></c>: merge requests in the same stage deploy together,
    /// which violates an ordering constraint exactly as surely as the reverse order does.
    /// </remarks>
    public static IReadOnlyList<PlanEdge> ViolatedBy(
        IReadOnlyList<MergeRequest> mrs, IReadOnlyDictionary<Guid, int> stageOf) =>
        MandatoryEdges(mrs)
            .Where(e => stageOf.TryGetValue(e.FromMrId, out var predecessorSeq)
                        && stageOf.TryGetValue(e.ToMrId, out var seq)
                        && predecessorSeq >= seq)
            .ToList();

    /// <summary>
    /// Collects every ordering constraint, deduplicated. When the same pair is
    /// implied by several links the most critical one wins, so a soft duplicate
    /// can never make a hard constraint droppable.
    /// </summary>
    private static Dictionary<(Guid From, Guid To), PlanEdgeKind> BuildEdges(
        IReadOnlyList<MergeRequest> mrs, HashSet<Guid> ids)
    {
        var byTask = mrs.Where(m => m.TaskId.HasValue).ToLookup(m => m.TaskId!.Value);
        var byStack = mrs
            .SelectMany(m => m.Repository.RepositoryStacks.Select(rs => (rs.StackId, Mr: m)))
            .ToLookup(x => x.StackId, x => x.Mr);

        var edges = new Dictionary<(Guid, Guid), PlanEdgeKind>();

        void AddEdge(Guid from, Guid to, PlanEdgeKind kind)
        {
            // A self-edge means the same MR sits on both ends of a constraint (e.g. its
            // repository is in two stacks that depend on each other). It carries no
            // ordering information and would deadlock Kahn's algorithm.
            if (from == to || !ids.Contains(from) || !ids.Contains(to)) return;

            if (!edges.TryGetValue((from, to), out var existing) || kind < existing)
                edges[(from, to)] = kind;
        }

        foreach (var mr in mrs)
        {
            // task.Dependencies == rows where DependentTaskId == task.Id, i.e. the tasks
            // this one waits on. Every MR of a predecessor task deploys first — a task
            // may span several repositories, so all of its MRs are predecessors.
            foreach (var dep in mr.Task?.Dependencies ?? [])
                foreach (var predecessor in byTask[dep.DependsOnTaskId])
                    AddEdge(predecessor.Id, mr.Id, PlanEdgeKind.TaskDependency);

            foreach (var rs in mr.Repository.RepositoryStacks)
                foreach (var stackDep in rs.Stack.DependentOn)
                {
                    var kind = stackDep.Type == StackDependencyType.Hard
                        ? PlanEdgeKind.StackHard
                        : PlanEdgeKind.StackSoft;

                    foreach (var predecessor in byStack[stackDep.ToStackId])
                        AddEdge(predecessor.Id, mr.Id, kind);
                }
        }

        return edges;
    }

    /// <summary>
    /// Drops the least critical edge of each cycle until the graph is acyclic,
    /// reporting every drop. An all-hard cycle cannot be honoured at all; we still
    /// break it so the operator gets a plan, but flag it as unresolvable.
    /// </summary>
    private static List<PlanConflict> BreakCycles(
        Dictionary<(Guid From, Guid To), PlanEdgeKind> edges,
        HashSet<Guid> ids,
        Dictionary<Guid, int> rank)
    {
        var conflicts = new List<PlanConflict>();

        // Every iteration removes one edge, so the edge count bounds the loop.
        for (int guard = edges.Count; guard >= 0; guard--)
        {
            var cycle = FindCycle(edges, ids, rank);
            if (cycle is null) return conflicts;

            var victim = cycle
                .OrderByDescending(e => (int)e.Kind)
                .ThenBy(e => rank[e.FromMrId])
                .ThenBy(e => rank[e.ToMrId])
                .First();

            edges.Remove((victim.FromMrId, victim.ToMrId));
            conflicts.Add(new PlanConflict(
                victim.Kind,
                victim.FromMrId,
                victim.ToMrId,
                victim.Kind == PlanEdgeKind.StackHard
                    ? "Dependency cycle consists only of hard links and cannot be satisfied. "
                      + "This link was dropped to produce an ordered plan; fix the stack configuration."
                    : $"Dropped to break a dependency cycle ({victim.Kind})."));
        }

        return conflicts;
    }

    /// <summary>Iterative DFS — recursion would risk a stack overflow on deep graphs.</summary>
    private static List<PlanEdge>? FindCycle(
        Dictionary<(Guid From, Guid To), PlanEdgeKind> edges,
        HashSet<Guid> ids,
        Dictionary<Guid, int> rank)
    {
        var adjacency = BuildAdjacency(edges, ids, rank);

        const int White = 0, Grey = 1, Black = 2;
        var colour = ids.ToDictionary(id => id, _ => White);
        var cursor = new Dictionary<Guid, int>();
        var parent = new Dictionary<Guid, PlanEdge>();

        foreach (var start in ids.OrderBy(id => rank[id]))
        {
            if (colour[start] != White) continue;

            var stack = new Stack<Guid>();
            stack.Push(start);
            colour[start] = Grey;
            cursor[start] = 0;

            while (stack.Count > 0)
            {
                var u = stack.Peek();
                var outgoing = adjacency[u];

                if (cursor[u] < outgoing.Count)
                {
                    var edge = outgoing[cursor[u]++];
                    var v = edge.ToMrId;

                    if (colour[v] == Grey)
                    {
                        // Back edge: v -> ... -> u -> v. Walk parents from u back to v.
                        var cycle = new List<PlanEdge> { edge };
                        for (var node = u; node != v; node = parent[node].FromMrId)
                            cycle.Add(parent[node]);
                        return cycle;
                    }

                    if (colour[v] == White)
                    {
                        colour[v] = Grey;
                        cursor[v] = 0;
                        parent[v] = edge;
                        stack.Push(v);
                    }
                }
                else
                {
                    colour[u] = Black;
                    stack.Pop();
                }
            }
        }

        return null;
    }

    /// <summary>Kahn's algorithm. Each level is one stage: its MRs deploy in parallel.</summary>
    private static List<List<Guid>> TopoSort(
        Dictionary<(Guid From, Guid To), PlanEdgeKind> edges,
        HashSet<Guid> ids,
        Dictionary<Guid, int> rank)
    {
        var adjacency = BuildAdjacency(edges, ids, rank);
        var inDegree = ids.ToDictionary(id => id, _ => 0);
        foreach (var ((_, to), _) in edges) inDegree[to]++;

        var stages = new List<List<Guid>>();
        var ready = ids.Where(id => inDegree[id] == 0).OrderBy(id => rank[id]).ToList();

        while (ready.Count > 0)
        {
            stages.Add(ready);

            var next = new List<Guid>();
            foreach (var node in ready)
                foreach (var edge in adjacency[node])
                    if (--inDegree[edge.ToMrId] == 0)
                        next.Add(edge.ToMrId);

            ready = next.OrderBy(id => rank[id]).ToList();
        }

        // BreakCycles guarantees acyclicity, so every node is placed. Guard against a
        // silently truncated plan rather than shipping one that omits merge requests.
        var placed = stages.Sum(s => s.Count);
        if (placed != ids.Count)
            throw new InvalidOperationException(
                $"Release plan topological sort placed {placed} of {ids.Count} merge requests; "
                + "the dependency graph is still cyclic.");

        return stages;
    }

    private static Dictionary<Guid, List<PlanEdge>> BuildAdjacency(
        Dictionary<(Guid From, Guid To), PlanEdgeKind> edges,
        HashSet<Guid> ids,
        Dictionary<Guid, int> rank)
    {
        var adjacency = ids.ToDictionary(id => id, _ => new List<PlanEdge>());
        foreach (var ((from, to), kind) in edges)
            adjacency[from].Add(new PlanEdge(from, to, kind));

        foreach (var list in adjacency.Values)
            list.Sort((a, b) => rank[a.ToMrId].CompareTo(rank[b.ToMrId]));

        return adjacency;
    }
}
