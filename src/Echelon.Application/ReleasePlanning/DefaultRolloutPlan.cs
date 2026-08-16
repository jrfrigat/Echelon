namespace Echelon.Application.ReleasePlanning;

/// <summary>A repository and the repositories it deploys after, as the default plan reads them.</summary>
/// <param name="Id">The repository.</param>
/// <param name="DependsOn">Its repository-ordering links.</param>
public record DefaultPlanRepository(Guid Id, IReadOnlyList<PlanRepositoryLink> DependsOn);

/// <summary>
/// The deploy order the repository-ordering rules imply, plus any rule that had to be dropped to
/// produce one.
/// </summary>
/// <param name="Waves">
/// Repository ids per wave: everything in a wave deploys in parallel, and each wave waits for the
/// one before it.
/// </param>
/// <param name="Conflicts">
/// Rules the ordering could not honour - in practice a cycle in the configuration, which is a
/// misconfiguration an operator can only fix if they are told about it.
/// </param>
public record DefaultPlanResult(List<List<Guid>> Waves, List<PlanConflict> Conflicts);

/// <summary>
/// Derives the "default rollout plan": the order repositories deploy in when nothing task-specific
/// says otherwise.
/// </summary>
/// <remarks>
/// This is what the repository-ordering rules mean, shown as an order rather than as a list of
/// pairs. An operator states rules one edge at a time ("api after db"), but what they are actually
/// configuring is a sequence, and a set of pairs does not read as one - least of all when the rules
/// contradict each other.
///
/// Pure: no EF, no I/O, no clock.
/// </remarks>
public static class DefaultRolloutPlan
{
    /// <summary>Orders the repositories by their ordering rules.</summary>
    /// <param name="repositories">
    /// Every repository, in a deterministic order - that order breaks ties within a wave, so the
    /// same configuration always renders the same plan.
    /// </param>
    /// <remarks>
    /// Built by handing the real ordering engine one stand-in merge request per repository, rather
    /// than by sorting here. The engine orders merge requests, and the default plan is precisely
    /// what it would produce if every repository held exactly one - including how it breaks a cycle
    /// (soft rules yield before hard ones) and what it reports having dropped. A second sort written
    /// for the preview could disagree with the planner, and the preview is the thing an operator
    /// trusts when deciding whether the configuration is right.
    ///
    /// The stand-in's id is the repository's own, so the waves come back as repository ids.
    /// </remarks>
    public static DefaultPlanResult Build(IReadOnlyList<DefaultPlanRepository> repositories)
    {
        var standIns = repositories
            .Select(r => new PlanMergeRequest(
                Id: r.Id,
                TaskId: null,
                DependsOnTaskIds: [],
                ChildTaskIds: [],
                RepositoryId: r.Id,
                RepositoryDependsOn: r.DependsOn))
            .ToList();

        var ordered = ReleasePlanGraph.Build(standIns);
        return new DefaultPlanResult(ordered.Stages, ordered.Conflicts);
    }
}
