using ReleaseOrchestrator.Core.Enums;

namespace ReleaseOrchestrator.Application.ReleasePlanning;

/// <summary>
/// What a rollout of a task waits for: its subtasks, the tasks it declares a dependency on, or both,
/// and whether one whole group goes before the other.
/// </summary>
/// <remarks>
/// <para>
/// This is an INPUT to the planner, never part of the plan it produces. Every ingestion event
/// rebuilds every active plan, so a policy stored on the generated plan would be rewritten by the
/// next event that touched anything; kept as an input, it survives recalculation by construction —
/// the same reason the ordering rules and the readiness rules live where they do.
/// </para>
/// <para>
/// The distinction the two flags draw is the one a survey of trackers arrived at:
/// <c>depends on</c>/<c>blocks</c> is the only link every tracker agrees is an ordering, while
/// <c>parent</c>/<c>subtask</c> is a hierarchy that may or may not imply one. Treating the hierarchy
/// as ordering unconditionally — which the planner used to do — is right for a parent that is an
/// umbrella over its children's work and wrong for one that merely groups unrelated tickets. Now it
/// is a decision rather than an assumption.
/// </para>
/// </remarks>
/// <param name="WaitForSubtasks">
/// Whether a parent waits for the tasks beneath it. Off means a parent can roll out while a subtask
/// has not — appropriate when the hierarchy is filing rather than composition.
/// </param>
/// <param name="WaitForLinked">
/// Whether a task waits for the tasks it declares a dependency on. Off means declared dependencies
/// are ignored for ordering; the tasks stay out of the closure entirely.
/// </param>
/// <param name="GroupOrder">
/// Whether one kind of prerequisite deploys entirely before the other. See
/// <see cref="PrerequisiteGroupOrder"/>; the default imposes nothing.
/// </param>
public sealed record TaskWaitPolicy(
    bool WaitForSubtasks = true,
    bool WaitForLinked = true,
    PrerequisiteGroupOrder GroupOrder = PrerequisiteGroupOrder.Together)
{
    /// <summary>
    /// What the planner did before the policy existed: wait for everything, order nothing extra.
    /// </summary>
    /// <remarks>
    /// Named rather than left as the constructor's defaults so an upgrade is visibly a no-op: an
    /// installation that configures nothing plans exactly as it did before.
    /// </remarks>
    public static TaskWaitPolicy Default { get; } = new();

    /// <summary>
    /// Applies a task's own answers over this one. A null answer means "inherit", which is what lets
    /// a global default exist at all.
    /// </summary>
    /// <param name="waitForSubtasks">The task's answer, or null to inherit.</param>
    /// <param name="waitForLinked">The task's answer, or null to inherit.</param>
    /// <param name="groupOrder">The task's answer, or null to inherit.</param>
    public TaskWaitPolicy OverriddenBy(
        bool? waitForSubtasks, bool? waitForLinked, PrerequisiteGroupOrder? groupOrder) =>
        new(waitForSubtasks ?? WaitForSubtasks,
            waitForLinked ?? WaitForLinked,
            groupOrder ?? GroupOrder);
}
