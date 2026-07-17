using ReleaseOrchestrator.Application.DTOs;
using ReleaseOrchestrator.Application.Exceptions;
using ReleaseOrchestrator.Core.Enums;
using Xunit;

namespace ReleaseOrchestrator.UnitTests.ReleasePlanning;

/// <summary>
/// The drag-and-drop editing API. It used to accept anything in silence while YAML import refused
/// the same edit outright, so the constraint an import could not get past was a drag away.
/// </summary>
/// <remarks>
/// Both paths now answer to one rule: a plan may violate a constraint — an operator sometimes has
/// to deploy against the declared order — but it may never report itself clean while doing so.
/// </remarks>
public sealed class ReleasePlanManualEditTests : PlannerTestBase
{
    /// <summary>A plan where db!10 must deploy before backend!20, and does.</summary>
    private async Task<ReleasePlanDto> PlanWithHardStackLinkAsync(StackDependencyType type = StackDependencyType.Hard)
    {
        var database = AddRepository("db");
        var backend = AddRepository("backend");
        AddStackDependency(from: AddStack("backend", backend), to: AddStack("database", database), type);
        AddMergeRequest(database, externalId: "10");
        AddMergeRequest(backend, externalId: "20");
        await Db.SaveChangesAsync(Ct);

        return await Planner().RecalculateAsync(Ct);
    }

    private static StageItemDto ItemIn(ReleasePlanDto plan, int sequence) =>
        plan.Stages.Single(s => s.Sequence == sequence).Items.Single();

    private static Guid StageId(ReleasePlanDto plan, int sequence) =>
        plan.Stages.Single(s => s.Sequence == sequence).Id;

    /// <summary>
    /// The hole this suite exists for: dragging the database migration into the backend's stage
    /// made them deploy together, and the plan came back reporting zero conflicts.
    /// </summary>
    [Fact]
    public async Task MovingAnItemPastAHardDependencyIsAllowedButRecorded()
    {
        var plan = await PlanWithHardStackLinkAsync();
        var databaseItem = ItemIn(plan, 1);

        var after = await Planner().MoveItemAsync(plan.Id, databaseItem.Id, StageId(plan, 2), Ct);

        // The move stands — the operator decided.
        Assert.Empty(after.Stages.Single(s => s.Sequence == 1).Items);
        Assert.Equal(2, after.Stages.Single(s => s.Sequence == 2).Items.Count);

        // But the plan no longer claims to be clean.
        var conflict = Assert.Single(after.Conflicts);
        Assert.Equal(nameof(Application.ReleasePlanning.PlanEdgeKind.StackHard), conflict.Kind);
        Assert.Contains("db!10", conflict.Reason);
    }

    /// <summary>
    /// Conflicts are stored, not computed per response: a reader fetching the plan later must see
    /// the same violation as the operator who caused it.
    /// </summary>
    [Fact]
    public async Task ARecordedConflictSurvivesReloading()
    {
        var plan = await PlanWithHardStackLinkAsync();
        var planner = Planner();
        await planner.MoveItemAsync(plan.Id, ItemIn(plan, 1).Id, StageId(plan, 2), Ct);

        var reloaded = await planner.GetByIdAsync(plan.Id, Ct);

        Assert.Single(reloaded!.Conflicts);
    }

    /// <summary>
    /// A conflict that outlives the edit that caused it is a false alarm, and false alarms are how
    /// a conflict list stops being read.
    /// </summary>
    [Fact]
    public async Task MovingAnItemBackClearsTheConflict()
    {
        var plan = await PlanWithHardStackLinkAsync();
        var planner = Planner();
        var databaseItem = ItemIn(plan, 1);

        var broken = await planner.MoveItemAsync(plan.Id, databaseItem.Id, StageId(plan, 2), Ct);
        Assert.Single(broken.Conflicts);

        var restored = await planner.MoveItemAsync(plan.Id, databaseItem.Id, StageId(plan, 1), Ct);

        Assert.Empty(restored.Conflicts);
    }

    [Fact]
    public async Task ReorderingStagesPastAHardDependencyIsRecorded()
    {
        var plan = await PlanWithHardStackLinkAsync();
        var planner = Planner();

        // Swap the two stages: backend's stage becomes 1, the database's becomes 2.
        var reordered = await planner.ReorderStagesAsync(
            plan.Id, [StageId(plan, 2), StageId(plan, 1)], Ct);

        Assert.Single(reordered.Conflicts);
    }

    [Fact]
    public async Task RemovingTheItemThatCausedAConflictClearsIt()
    {
        var plan = await PlanWithHardStackLinkAsync();
        var planner = Planner();
        var databaseItem = ItemIn(plan, 1);

        var broken = await planner.MoveItemAsync(plan.Id, databaseItem.Id, StageId(plan, 2), Ct);
        Assert.Single(broken.Conflicts);

        // With one side of the constraint gone, the constraint no longer applies to this plan.
        var after = await planner.RemoveItemAsync(plan.Id, databaseItem.Id, Ct);

        Assert.Empty(after.Conflicts);
    }

    /// <summary>
    /// Adding a merge request the planner left out is the point of manual editing; dropping it into
    /// a stage that breaks its ordering is the risk.
    /// </summary>
    [Fact]
    public async Task AddingAnItemIntoAStageThatBreaksItsOrderingIsRecorded()
    {
        var repo = AddRepository("api");
        var first = AddTask("TASK-1");
        var second = AddTask("TASK-2");
        AddTaskDependency(dependent: second, dependsOn: first);
        var firstMr = AddMergeRequest(repo, first, status: MergeRequestStatus.Opened);
        AddMergeRequest(repo, second);
        await Db.SaveChangesAsync(Ct);

        var planner = Planner();

        // Only TASK-2's merge request is ready, so the plan has one stage and no constraint in it.
        var plan = await planner.RecalculateAsync(Ct);
        Assert.Empty(plan.Conflicts);

        // TASK-1's merge request is added by hand, into the same stage as the task waiting on it.
        var after = await planner.AddItemAsync(plan.Id, StageId(plan, 1), firstMr.Id, Ct);

        var conflict = Assert.Single(after.Conflicts);
        Assert.Equal(nameof(Application.ReleasePlanning.PlanEdgeKind.TaskDependency), conflict.Kind);
    }

    [Fact]
    public async Task OverridingASoftStackLinkByHandIsNotAConflict()
    {
        var plan = await PlanWithHardStackLinkAsync(StackDependencyType.Soft);

        var after = await Planner().MoveItemAsync(plan.Id, ItemIn(plan, 1).Id, StageId(plan, 2), Ct);

        Assert.Empty(after.Conflicts);
    }

    [Fact]
    public async Task AnEditStampsThePlanAsUpdated()
    {
        var plan = await PlanWithHardStackLinkAsync();

        var after = await Planner().MoveItemAsync(plan.Id, ItemIn(plan, 1).Id, StageId(plan, 2), Ct);

        Assert.True(after.Stages.Single(s => s.Sequence == 2).IsManualOverride);
        Assert.Equal(Now, after.UpdatedAt);
    }

    [Fact]
    public async Task AnItemFromAnotherPlanIsNotFound()
    {
        var plan = await PlanWithHardStackLinkAsync();

        await Assert.ThrowsAsync<NotFoundException>(
            () => Planner().MoveItemAsync(plan.Id, Guid.NewGuid(), StageId(plan, 2), Ct));
    }

    [Fact]
    public async Task MovingToAStageOutsideThePlanIsNotFound()
    {
        var plan = await PlanWithHardStackLinkAsync();

        await Assert.ThrowsAsync<NotFoundException>(
            () => Planner().MoveItemAsync(plan.Id, ItemIn(plan, 1).Id, Guid.NewGuid(), Ct));
    }

    /// <summary>
    /// A partial reorder renumbers some stages from 1 while the rest keep their old numbers, which
    /// silently gives two stages the same sequence.
    /// </summary>
    [Fact]
    public async Task AReorderMustListEveryStageExactlyOnce()
    {
        var plan = await PlanWithHardStackLinkAsync();
        var planner = Planner();

        await Assert.ThrowsAsync<DomainValidationException>(
            () => planner.ReorderStagesAsync(plan.Id, [StageId(plan, 1)], Ct));

        await Assert.ThrowsAsync<DomainValidationException>(
            () => planner.ReorderStagesAsync(plan.Id, [StageId(plan, 1), StageId(plan, 1)], Ct));
    }
}
