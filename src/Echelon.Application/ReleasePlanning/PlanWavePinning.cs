namespace Echelon.Application.ReleasePlanning;

/// <summary>The edge deltas that make the derivation reproduce an imported wave assignment.</summary>
/// <param name="Add">Edges to pin each merge request to its wave.</param>
/// <param name="Remove">Derived edges the assignment contradicts.</param>
public sealed record PlanWaveDeltas(
    IReadOnlyList<(Guid From, Guid To)> Add,
    IReadOnlyList<(Guid From, Guid To)> Remove);

/// <summary>
/// Turns "these merge requests deploy in these waves" into the deltas the planner already replays.
/// </summary>
/// <remarks>
/// <para>
/// This is the whole of import, minus resolving names. An imported plan is not stored as a plan - plan
/// rows are rebuilt on every ingestion event and would take the import with them. It is stored as the
/// same <c>AddEdge</c>/<c>RemoveEdge</c> deltas an operator's drag-and-drop produces, so the next
/// recalculation reproduces it from the atlas instead of preserving a frozen answer. One derivation,
/// three ways in (006 §1).
/// </para>
/// <para>
/// Pure and separately testable, because the argument for why it is correct is a graph argument and
/// not a database one.
/// </para>
/// </remarks>
public static class PlanWavePinning
{
    /// <summary>
    /// Computes the deltas for one wave assignment.
    /// </summary>
    /// <param name="mrs">
    /// The plan's merge requests in the planner's own order - it decides which predecessor is chosen
    /// when several would do, so the same document must always give the same deltas.
    /// </param>
    /// <param name="derivedEdges">Every edge the derivation produces before overrides.</param>
    /// <param name="waveOf">The wave each merge request is to deploy in, 1-based and contiguous.</param>
    /// <remarks>
    /// <para>
    /// Two moves, and between them they pin the assignment exactly:
    /// </para>
    /// <para>
    /// REMOVE every derived edge whose ends are not in increasing wave order. After this, every
    /// surviving predecessor of a merge request sits in a strictly earlier wave, so nothing can push
    /// it later than the document says.
    /// </para>
    /// <para>
    /// ADD one edge from the previous wave to any merge request that has no surviving predecessor
    /// there. Waves come from longest-path layering - a merge request lands one past its latest
    /// predecessor - so this is what stops it floating earlier than the document says.
    /// </para>
    /// <para>
    /// The result is a layered graph: every edge goes from wave k to a strictly higher wave. A layered
    /// graph cannot contain a cycle, so this never manufactures a conflict of its own; the only
    /// conflicts an import produces are the constraints its author chose to override, which the graph
    /// reports separately.
    /// </para>
    /// <para>
    /// Minimal on purpose. Pinning by adding every pair between consecutive waves would work and would
    /// leave the task carrying a quadratic pile of deltas that no operator could read or edit - and
    /// they are stored, so they are read.
    /// </para>
    /// </remarks>
    public static PlanWaveDeltas Compute(
        IReadOnlyList<PlanMergeRequest> mrs,
        IReadOnlyList<PlanEdge> derivedEdges,
        IReadOnlyDictionary<Guid, int> waveOf)
    {
        var remove = new List<(Guid From, Guid To)>();
        var add = new List<(Guid From, Guid To)>();

        // A predecessor whose wave is unknown cannot be reasoned about; leaving such an edge in place
        // is the safe direction, since the assignment says nothing about it.
        bool Known(Guid id) => waveOf.ContainsKey(id);

        var survivingPredecessorWaves = mrs.ToDictionary(mr => mr.Id, _ => new List<int>());

        foreach (var edge in derivedEdges)
        {
            if (!Known(edge.FromMrId) || !Known(edge.ToMrId)) continue;

            if (waveOf[edge.FromMrId] >= waveOf[edge.ToMrId])
                remove.Add((edge.FromMrId, edge.ToMrId));
            else if (survivingPredecessorWaves.TryGetValue(edge.ToMrId, out var waves))
                waves.Add(waveOf[edge.FromMrId]);
        }

        // First in the planner's order, so the choice is reproducible rather than dictionary order.
        var firstOfWave = new Dictionary<int, Guid>();
        foreach (var mr in mrs)
            if (Known(mr.Id)) firstOfWave.TryAdd(waveOf[mr.Id], mr.Id);

        foreach (var mr in mrs)
        {
            if (!Known(mr.Id)) continue;

            var wave = waveOf[mr.Id];
            if (wave <= 1) continue;

            if (survivingPredecessorWaves[mr.Id].Contains(wave - 1)) continue;

            // Contiguity is checked before this runs, so the previous wave is never empty. Guarded
            // anyway: silently skipping would produce a plan that quietly disagrees with its document.
            if (firstOfWave.TryGetValue(wave - 1, out var predecessor) && predecessor != mr.Id)
                add.Add((predecessor, mr.Id));
        }

        return new PlanWaveDeltas(add, remove);
    }
}
