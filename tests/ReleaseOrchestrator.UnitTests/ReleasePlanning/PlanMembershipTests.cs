using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using ReleaseOrchestrator.Core.Enums;
using ReleaseOrchestrator.Infrastructure.Persistence.Models;
using ReleaseOrchestrator.Infrastructure.ReleasePlanning;
using Xunit;

namespace ReleaseOrchestrator.UnitTests.ReleasePlanning;

/// <summary>
/// Forcing a merge request into or out of a task's rollout.
/// </summary>
/// <remarks>
/// Membership is the second thing about a plan an operator can state that the atlas does not decide
/// (the first is the wave assignment). Like the ordering deltas it is stored against the TASK and
/// replayed on every build — a decision recorded on the plan would last until the next webhook, and
/// every ingestion event is a webhook.
/// </remarks>
public class PlanMembershipTests : PlannerTestBase
{
    private RolloutPlanner NewPlanner() =>
        new(Db, new FakeTimeProvider(Now), NullLogger<RolloutPlanner>.Instance);

    private void AddMembershipOverride(TaskItem task, MergeRequest mr, PlanOverrideKind kind) =>
        Db.PlanOverrides.Add(new PlanOverride
        {
            Id = Guid.NewGuid(),
            TaskId = task.Id,
            Kind = kind,
            Payload = JsonSerializer.Serialize(new { MergeRequestId = mr.Id })
        });

    private static IEnumerable<Guid> MergeRequestsIn(Application.DTOs.RolloutPlanDto plan) =>
        plan.Nodes.SelectMany(n => n.Items).Select(i => i.MergeRequestId);

    [Fact]
    public async Task ExcludedMergeRequestLeavesThePlan()
    {
        var task = AddTask("PROJ-1");
        var api = AddRepository("api");
        var web = AddRepository("web");
        var kept = AddMergeRequest(api, task);
        var dropped = AddMergeRequest(web, task);
        await Db.SaveChangesAsync(Ct);

        var planner = NewPlanner();
        var before = await planner.RecalculateAsync(task.Id, actor: null, Ct);
        Assert.Equal(2, MergeRequestsIn(before).Count());

        AddMembershipOverride(task, dropped, PlanOverrideKind.ExcludeMr);
        await Db.SaveChangesAsync(Ct);

        var after = await planner.RecalculateAsync(task.Id, actor: null, Ct);

        Assert.Equal([kept.Id], MergeRequestsIn(after));
    }

    /// <summary>
    /// The exclusion is replayed on every rebuild, not consumed by the first one.
    /// </summary>
    /// <remarks>
    /// The failure this guards against is quiet and expensive: the merge request comes back on the
    /// next ingestion event and ships in a rollout somebody had deliberately kept it out of.
    /// </remarks>
    [Fact]
    public async Task ExclusionSurvivesFurtherRecalculations()
    {
        var task = AddTask("PROJ-1");
        var dropped = AddMergeRequest(AddRepository("web"), task);
        AddMergeRequest(AddRepository("api"), task);
        await Db.SaveChangesAsync(Ct);

        AddMembershipOverride(task, dropped, PlanOverrideKind.ExcludeMr);
        await Db.SaveChangesAsync(Ct);

        var planner = NewPlanner();
        await planner.RecalculateAsync(task.Id, actor: null, Ct);
        await planner.RecalculateAsync(task.Id, actor: null, Ct);
        var third = await planner.RecalculateAsync(task.Id, actor: null, Ct);

        Assert.DoesNotContain(dropped.Id, MergeRequestsIn(third));
    }

    /// <summary>A closed merge request is out of the plan by derivation, and can be forced back in.</summary>
    [Fact]
    public async Task IncludedMergeRequestRejoinsThePlanAndIsMarked()
    {
        var task = AddTask("PROJ-1");
        var closed = AddMergeRequest(AddRepository("web"), task, status: MergeRequestStatus.Closed);
        AddMergeRequest(AddRepository("api"), task);
        await Db.SaveChangesAsync(Ct);

        var planner = NewPlanner();
        var before = await planner.RecalculateAsync(task.Id, actor: null, Ct);
        Assert.DoesNotContain(closed.Id, MergeRequestsIn(before));

        AddMembershipOverride(task, closed, PlanOverrideKind.IncludeMr);
        await Db.SaveChangesAsync(Ct);

        var after = await planner.RecalculateAsync(task.Id, actor: null, Ct);

        var item = after.Nodes.SelectMany(n => n.Items).Single(i => i.MergeRequestId == closed.Id);
        Assert.True(item.ManuallyIncluded, "a forced merge request must say so, or the plan looks derived");
    }

    /// <summary>
    /// Contradictory deltas resolve to exclusion — the conservative direction.
    /// </summary>
    /// <remarks>
    /// "Do not deploy this" is the stronger statement: something not shipping is recoverable in a way
    /// that something shipping is not. The endpoint clears both kinds before writing one, so this is
    /// a guard against rows that predate that, not a state it hands out.
    /// </remarks>
    [Fact]
    public async Task ExclusionWinsOverInclusion()
    {
        var task = AddTask("PROJ-1");
        var contested = AddMergeRequest(AddRepository("web"), task);
        AddMergeRequest(AddRepository("api"), task);
        await Db.SaveChangesAsync(Ct);

        AddMembershipOverride(task, contested, PlanOverrideKind.IncludeMr);
        AddMembershipOverride(task, contested, PlanOverrideKind.ExcludeMr);
        await Db.SaveChangesAsync(Ct);

        var plan = await NewPlanner().RecalculateAsync(task.Id, actor: null, Ct);

        Assert.DoesNotContain(contested.Id, MergeRequestsIn(plan));
    }

    /// <summary>An unreadable delta is skipped, not thrown: a bad row must not make a task unplannable.</summary>
    [Fact]
    public async Task UnreadableMembershipDeltaIsIgnored()
    {
        var task = AddTask("PROJ-1");
        var mr = AddMergeRequest(AddRepository("api"), task);
        Db.PlanOverrides.Add(new PlanOverride
        {
            Id = Guid.NewGuid(),
            TaskId = task.Id,
            Kind = PlanOverrideKind.ExcludeMr,
            Payload = "{ this is not json"
        });
        await Db.SaveChangesAsync(Ct);

        var plan = await NewPlanner().RecalculateAsync(task.Id, actor: null, Ct);

        Assert.Equal([mr.Id], MergeRequestsIn(plan));
    }

    /// <summary>Excluding everything is allowed, and leaves a plan with nothing in it.</summary>
    /// <remarks>
    /// Launching such a plan is refused by <c>RolloutService</c> rather than here: an empty plan is a
    /// legitimate intermediate state (a task whose merge requests have not arrived yet looks the
    /// same), and it is only at launch that it becomes a run nothing can ever advance.
    /// </remarks>
    [Fact]
    public async Task ExcludingEverythingLeavesAnEmptyPlan()
    {
        var task = AddTask("PROJ-1");
        var only = AddMergeRequest(AddRepository("api"), task);
        await Db.SaveChangesAsync(Ct);

        AddMembershipOverride(task, only, PlanOverrideKind.ExcludeMr);
        await Db.SaveChangesAsync(Ct);

        var plan = await NewPlanner().RecalculateAsync(task.Id, actor: null, Ct);

        Assert.Empty(MergeRequestsIn(plan));
        Assert.Empty(await Db.PlanItems.ToListAsync(Ct));
    }
}
