using System.Linq.Expressions;
using ReleaseOrchestrator.Application.ReleasePlanning;
using ReleaseOrchestrator.Infrastructure.Persistence.Models;

namespace ReleaseOrchestrator.Infrastructure.ReleasePlanning;

/// <summary>
/// Reads a merge request into the shape the planner orders by.
/// </summary>
/// <remarks>
/// The one place the persistence model meets the ordering algorithm, and it is an expression so
/// the database does the work: EF turns it into joins and returns the columns named here, rather
/// than materialising entities and walking navigations in memory.
///
/// It replaces a chain of <c>Include</c>/<c>ThenInclude</c> whose failure mode was silence. Drop a
/// link from that chain and the navigation was empty rather than absent - an ordering built from
/// half its constraints, indistinguishable from a correct one. Drop a field from this projection
/// and the compiler names it.
/// </remarks>
internal static class PlanInput
{
    /// <summary>Usable inside a <c>Select</c>, so it never runs on the client.</summary>
    public static readonly Expression<Func<MergeRequest, PlanMergeRequest>> FromEntity =
        mr => new PlanMergeRequest(
            mr.Id,
            mr.TaskId,
            // Rows naming this merge request's task as the dependent - the tasks it waits on.
            // Null Task yields no rows, which is what an unlinked merge request should contribute.
            mr.Task!.Dependencies.Select(d => d.DependsOnTaskId).ToList(),
            // The task's subtasks, which it also waits on: a parent deploys after the children it
            // covers. A second field rather than a concatenation of the two - EF cannot translate a
            // projection that concatenates two collection subqueries, and says so at query time
            // rather than at compile time. PlanMergeRequest.PrerequisiteTaskIds rejoins them.
            mr.Task.Children.Select(c => c.Id).ToList(),
            mr.RepositoryId,
            // Repository-ordering links: the editable deploy-order policy. Same failure mode if a
            // link is missing (an ordering built from half the constraints), so it is named here.
            mr.Repository.DependsOn
                .Select(rd => new PlanRepositoryLink(rd.ToRepositoryId, rd.Type))
                .ToList());
}
