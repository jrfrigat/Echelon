using Echelon.Core.Enums;

namespace Echelon.Application.ReleasePlanning;

/// <summary>
/// Why one merge request must be deployed before another, and how readily the
/// link may be dropped to break a cycle. Higher value = dropped first: soft repository links yield
/// before task links, and hard links never yield (docs/issues/006-per-task-planning.md).
/// </summary>
public enum PlanEdgeKind
{
    /// <summary>An operator-added edge. Most protected: dropped last when a cycle must be broken.</summary>
    Manual = -2,
    /// <summary>A hard repository-ordering link. Never dropped to break a cycle.</summary>
    RepoHard = -1,
    /// <summary>A task-dependency edge: every merge request of a prerequisite task deploys first.</summary>
    TaskDependency = 1,
    /// <summary>A soft repository-ordering link. Advisory: dropped first to break a cycle.</summary>
    RepoSoft = 3
}

/// <summary>A "deploy <see cref="FromMrId"/> before <see cref="ToMrId"/>" constraint.</summary>
public record PlanEdge(Guid FromMrId, Guid ToMrId, PlanEdgeKind Kind);

/// <summary>A constraint the planner had to drop to produce an ordered plan.</summary>
public record PlanConflict(PlanEdgeKind DroppedEdgeKind, Guid FromMrId, Guid ToMrId, string Reason);

/// <summary>Ordered deploy stages plus every constraint sacrificed to get them.</summary>
public record PlanGraphResult(List<List<Guid>> Stages, List<PlanConflict> Conflicts);

/// <summary>
/// Turns a set of deployable merge requests into ordered stages.
/// Pure: no EF, no I/O, no clock - the whole algorithm is unit-testable as-is.
/// </summary>
public static class ReleasePlanGraph
{
    /// <param name="mrs">
    /// Deployable merge requests, in a deterministic order - that order decides ties within a
    /// stage, so the same input has to give the same plan every time.
    /// </param>
    /// <param name="addEdges">
    /// Operator-added "deploy From before To" edges (a plan override). Endpoints outside
    /// <paramref name="mrs"/> and self-edges are ignored, as with derived edges.
    /// </param>
    /// <param name="removeEdges">
    /// Derived edges an operator dropped (a plan override). Removal runs before additions.
    /// </param>
    /// <param name="ruleEdges">
    /// Orderings an ordering-rule document produced, each carrying its own criticality.
    /// </param>
    /// <remarks>
    /// Rule edges are merged with the DERIVED set, not with the manual one, and that placement is the
    /// decision: a rule is a policy like the repository ordering it generalises, so it must be
    /// droppable by an operator's <paramref name="removeEdges"/> and must yield to a cycle according
    /// to its own hard/soft, rather than being protected like a hand-drawn edge.
    /// </remarks>
    public static PlanGraphResult Build(
        IReadOnlyList<PlanMergeRequest> mrs,
        IReadOnlyList<(Guid From, Guid To)>? addEdges = null,
        IReadOnlyList<(Guid From, Guid To)>? removeEdges = null,
        IReadOnlyList<PlanEdge>? ruleEdges = null)
    {
        var ids = mrs.Select(mr => mr.Id).ToHashSet();
        var rank = new Dictionary<Guid, int>(mrs.Count);
        for (int i = 0; i < mrs.Count; i++) rank[mrs[i].Id] = i;

        var edges = BuildEdges(mrs, ids);
        ApplyRuleEdges(edges, ids, ruleEdges);
        ApplyOverrides(edges, ids, addEdges, removeEdges);
        var conflicts = BreakCycles(edges, ids, rank);
        var stages = TopoSort(edges, ids, rank);

        ReportOverriddenConstraints(mrs, stages, conflicts);

        return new PlanGraphResult(stages, conflicts);
    }

    /// <summary>
    /// Records every mandatory constraint the finished order does not honour.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The other half of <see cref="ApplyOverrides"/>. Dropping a task or hard edge there reorders the
    /// plan and leaves no trace in the edge set - no cycle forms, so <see cref="BreakCycles"/> has
    /// nothing to report, and a plan that deploys against a hard dependency would come back looking
    /// clean. That is the one thing a plan may never do.
    /// </para>
    /// <para>
    /// Placed in <see cref="Build"/> rather than in the caller so it cannot be forgotten by a new one:
    /// an override arriving from the plan screen, from an ordering rule or from an imported document
    /// is checked by the same code, which is what "one derivation" has to mean here.
    /// </para>
    /// <para>
    /// Deduplicated against what the cycle-breaker already said: an edge dropped to break a cycle is
    /// necessarily violated by the resulting order, and reporting it twice would read as two problems.
    /// </para>
    /// </remarks>
    private static void ReportOverriddenConstraints(
        IReadOnlyList<PlanMergeRequest> mrs, List<List<Guid>> stages, List<PlanConflict> conflicts)
    {
        var stageOf = new Dictionary<Guid, int>();
        for (int i = 0; i < stages.Count; i++)
            foreach (var id in stages[i]) stageOf[id] = i;

        var reported = conflicts.Select(c => (c.FromMrId, c.ToMrId)).ToHashSet();

        foreach (var edge in ViolatedBy(mrs, stageOf))
        {
            if (!reported.Add((edge.FromMrId, edge.ToMrId))) continue;

            conflicts.Add(new PlanConflict(
                edge.Kind, edge.FromMrId, edge.ToMrId,
                $"The plan's order does not honour this {edge.Kind} constraint. An override or an "
                + "imported wave assignment put these merge requests in this order deliberately."));
        }
    }

    /// <summary>
    /// Orders the rollout of one target task: the closure of the target and its prerequisites,
    /// restricted to their merge requests, run through the same <see cref="Build"/> as the global plan.
    /// </summary>
    /// <param name="targetTaskId">The task to roll out.</param>
    /// <param name="taskDependsOn">
    /// Task-dependency adjacency (see <see cref="PlanClosureBuilder"/>): for each task, the tasks it
    /// deploys after.
    /// </param>
    /// <param name="allMergeRequests">
    /// Every candidate merge request, in a deterministic order -- that order breaks ties within a
    /// stage, exactly as in <see cref="Build"/>. Only those whose task is in the closure are kept.
    /// </param>
    /// <returns>Ordered deploy stages for the target's rollout, plus any constraint dropped to order them.</returns>
    /// <remarks>
    /// A projection, not a second algorithm: the per-task plan is the global graph cut down to one
    /// target's closure. Edges to merge requests outside the closure need no special handling --
    /// <see cref="Build"/> already ignores an edge whose endpoints it was not given.
    /// </remarks>
    public static PlanGraphResult BuildForTask(
        Guid targetTaskId,
        IReadOnlyDictionary<Guid, IReadOnlyList<Guid>> taskDependsOn,
        IReadOnlyList<PlanMergeRequest> allMergeRequests,
        IReadOnlyList<(Guid From, Guid To)>? addEdges = null,
        IReadOnlyList<(Guid From, Guid To)>? removeEdges = null)
    {
        var closure = PlanClosureBuilder.Closure(targetTaskId, taskDependsOn).ToHashSet();

        // Filtering preserves the input order, so ties inside a stage resolve the same way as Build.
        var inClosure = allMergeRequests
            .Where(mr => mr.TaskId.HasValue && closure.Contains(mr.TaskId.Value))
            .ToList();

        return Build(inClosure, addEdges, removeEdges);
    }

    /// <summary>
    /// Merges the ordering-rule document's edges into the derived set.
    /// </summary>
    /// <remarks>
    /// Same guard and same precedence as a derived edge: endpoints outside this plan are ignored, and
    /// when a pair is stated twice the more critical statement wins - so a soft rule can never make an
    /// existing hard constraint droppable.
    /// </remarks>
    private static void ApplyRuleEdges(
        Dictionary<(Guid From, Guid To), PlanEdgeKind> edges,
        HashSet<Guid> ids,
        IReadOnlyList<PlanEdge>? ruleEdges)
    {
        if (ruleEdges is null) return;

        foreach (var edge in ruleEdges)
        {
            if (edge.FromMrId == edge.ToMrId
                || !ids.Contains(edge.FromMrId) || !ids.Contains(edge.ToMrId)) continue;

            var key = (edge.FromMrId, edge.ToMrId);
            if (!edges.TryGetValue(key, out var existing) || edge.Kind < existing)
                edges[key] = edge.Kind;
        }
    }

    /// <summary>
    /// Applies operator overrides to the derived edge set: <paramref name="removeEdges"/> drops a
    /// derived ordering, <paramref name="addEdges"/> adds a manual one. Removal runs first, so an
    /// operator can drop a derived edge and add a different one between the same pair in one pass.
    /// </summary>
    /// <remarks>
    /// The override changes the ordering the plan is BUILT from, not what <see cref="MandatoryEdges"/>
    /// reports: dropping a task or hard edge here reorders the plan, and <see cref="ViolatedBy"/> --
    /// which recomputes from <see cref="BuildEdges"/> -- still records it as a violation. That is the
    /// rule: a plan may deploy against a constraint, but never silently
    /// (docs/issues/006-per-task-planning.md).
    /// </remarks>
    private static void ApplyOverrides(
        Dictionary<(Guid From, Guid To), PlanEdgeKind> edges,
        HashSet<Guid> ids,
        IReadOnlyList<(Guid From, Guid To)>? addEdges,
        IReadOnlyList<(Guid From, Guid To)>? removeEdges)
    {
        if (removeEdges is not null)
            foreach (var edge in removeEdges)
                edges.Remove(edge);

        if (addEdges is not null)
            foreach (var (from, to) in addEdges)
            {
                // Same guard as the derived edges: a self-edge, or an endpoint we were not given,
                // carries no ordering and would only risk a deadlock in Kahn's algorithm.
                if (from == to || !ids.Contains(from) || !ids.Contains(to)) continue;
                edges[(from, to)] = PlanEdgeKind.Manual;
            }
    }

    /// <summary>
    /// Constraints an operator may not violate: hard repository links and task dependencies.
    /// Soft repository links are advisory and excluded. Used to vet imported plans
    /// (docs/issues/006-per-task-planning.md) against the same edge derivation the planner itself
    /// uses, so the two cannot drift - one derivation for import, edit and recalculate alike.
    /// </summary>
    public static IReadOnlyList<PlanEdge> MandatoryEdges(IReadOnlyList<PlanMergeRequest> mrs) =>
        DerivedEdges(mrs).Where(e => e.Kind != PlanEdgeKind.RepoSoft).ToList();

    /// <summary>
    /// Every ordering edge the derivation produces before any operator override - the constraints an
    /// imported plan has to be reconciled against.
    /// </summary>
    /// <param name="mrs">The plan's merge requests.</param>
    /// <param name="ruleEdges">The ordering-rule document's edges, which are derived too.</param>
    /// <remarks>
    /// Public because import needs the edge SET, not just the resulting order: turning a wave
    /// assignment into deltas means knowing exactly which derived edges contradict it. Deriving that
    /// set a second time in the importer is the drift 006 §1 forbids, so it is exposed from here.
    /// </remarks>
    public static IReadOnlyList<PlanEdge> DerivedEdges(
        IReadOnlyList<PlanMergeRequest> mrs, IReadOnlyList<PlanEdge>? ruleEdges = null)
    {
        var ids = mrs.Select(mr => mr.Id).ToHashSet();
        var edges = BuildEdges(mrs, ids);
        ApplyRuleEdges(edges, ids, ruleEdges);

        return edges.Select(kv => new PlanEdge(kv.Key.From, kv.Key.To, kv.Value)).ToList();
    }

    /// <summary>
    /// Mandatory constraints the given stage assignment does not honour.
    /// </summary>
    /// <param name="mrs">The plan's merge requests.</param>
    /// <param name="stageOf">Stage sequence per merge request id.</param>
    /// <remarks>
    /// A plan may violate a constraint - an operator sometimes has to deploy against the declared
    /// order - but it may never do so silently, so this is what turns an edit into a recorded
    /// conflict. Soft links are excluded: the planner drops them itself to break cycles, so
    /// reporting them would flag the operator for a choice the planner makes unprompted.
    ///
    /// Comparison is <c>>=</c>, not <c>></c>: merge requests in the same stage deploy together,
    /// which violates an ordering constraint exactly as surely as the reverse order does.
    /// </remarks>
    public static IReadOnlyList<PlanEdge> ViolatedBy(
        IReadOnlyList<PlanMergeRequest> mrs, IReadOnlyDictionary<Guid, int> stageOf) =>
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
        IReadOnlyList<PlanMergeRequest> mrs, HashSet<Guid> ids)
    {
        var byTask = mrs.Where(m => m.TaskId.HasValue).ToLookup(m => m.TaskId!.Value);
        var byRepo = mrs.ToLookup(m => m.RepositoryId);

        var edges = new Dictionary<(Guid, Guid), PlanEdgeKind>();

        void AddEdge(Guid from, Guid to, PlanEdgeKind kind)
        {
            // A self-edge means the same MR sits on both ends of a constraint (e.g. its
            // repository declares a dependency that resolves back to itself). It carries no
            // ordering information and would deadlock Kahn's algorithm.
            if (from == to || !ids.Contains(from) || !ids.Contains(to)) return;

            if (!edges.TryGetValue((from, to), out var existing) || kind < existing)
                edges[(from, to)] = kind;
        }

        foreach (var mr in mrs)
        {
            // Every merge request of a task this one waits on deploys first - a task may span
            // several repositories, so all of its merge requests are predecessors, not just one.
            //
            // PrerequisiteTaskIds, not DependsOnTaskIds: a declared dependency and a subtask are the
            // same constraint arriving by different routes, and both mean "that task goes first".
            foreach (var prerequisiteTaskId in mr.PrerequisiteTaskIds)
                foreach (var predecessor in byTask[prerequisiteTaskId])
                    AddEdge(predecessor.Id, mr.Id, PlanEdgeKind.TaskDependency);

            // Repository-ordering edges: every merge request in a repository this one's repository
            // waits on deploys first. This is the editable ordering policy that replaced stacks, and
            // it is also what orders merge requests within a single task -- task dependencies never do.
            foreach (var link in mr.RepositoryDependsOn)
            {
                var kind = link.Type == StackDependencyType.Hard
                    ? PlanEdgeKind.RepoHard
                    : PlanEdgeKind.RepoSoft;

                foreach (var predecessor in byRepo[link.ToRepositoryId])
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
                victim.Kind is PlanEdgeKind.RepoHard
                    ? "Dependency cycle consists only of hard links and cannot be satisfied. "
                      + "This link was dropped to produce an ordered plan; fix the ordering configuration."
                    : $"Dropped to break a dependency cycle ({victim.Kind})."));
        }

        return conflicts;
    }

    /// <summary>Iterative DFS - recursion would risk a stack overflow on deep graphs.</summary>
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
