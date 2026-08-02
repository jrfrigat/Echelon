using ReleaseOrchestrator.Application.ReleasePlanning;
using ReleaseOrchestrator.Core.Enums;
using Xunit;

namespace ReleaseOrchestrator.UnitTests.ReleasePlanning;

/// <summary>
/// What a rollout waits for, and what it does not.
/// </summary>
/// <remarks>
/// The planner used to merge declared dependencies and the task hierarchy unconditionally, so
/// "a parent waits for its subtasks" was an assumption with no way to decline it — right for a parent
/// that is an umbrella over its children's work, wrong for one that merely files unrelated tickets.
/// docs/issues/002 had already noted that only <c>depends on</c> is an ordering every tracker agrees
/// on. These pin the policy that replaced the assumption.
/// </remarks>
public class TaskWaitGraphTests
{
    static readonly Guid Parent = new("11111111-1111-1111-1111-111111111111");
    static readonly Guid SubA = new("22222222-2222-2222-2222-222222222222");
    static readonly Guid SubB = new("33333333-3333-3333-3333-333333333333");
    static readonly Guid LinkA = new("44444444-4444-4444-4444-444444444444");
    static readonly Guid LinkB = new("55555555-5555-5555-5555-555555555555");

    static Dictionary<Guid, IReadOnlyList<Guid>> Build(TaskWaitPolicy policy, params TaskPrerequisites[] tasks) =>
        TaskWaitGraph.Build(tasks, _ => policy);

    static TaskPrerequisites Task(
        Guid id, Guid[]? subtasks = null, Guid[]? linked = null, Guid[]? manualOrder = null) =>
        new(id, subtasks ?? [], linked ?? [], manualOrder ?? []);

    [Fact]
    public void ByDefaultATaskWaitsForBothItsSubtasksAndItsDeclaredDependencies()
    {
        var graph = Build(TaskWaitPolicy.Default, Task(Parent, subtasks: [SubA], linked: [LinkA]));

        Assert.Equal([SubA, LinkA], graph[Parent]);
    }

    [Fact]
    public void TurningOffSubtasksLeavesOnlyTheDeclaredDependencies()
    {
        // The case the policy exists for: a parent that files tickets rather than composing work.
        var graph = Build(
            TaskWaitPolicy.Default with { WaitForSubtasks = false },
            Task(Parent, subtasks: [SubA], linked: [LinkA]));

        Assert.Equal([LinkA], graph[Parent]);
    }

    [Fact]
    public void TurningOffLinkedLeavesOnlyTheSubtasks()
    {
        var graph = Build(
            TaskWaitPolicy.Default with { WaitForLinked = false },
            Task(Parent, subtasks: [SubA], linked: [LinkA]));

        Assert.Equal([SubA], graph[Parent]);
    }

    [Fact]
    public void ATaskWaitingForNothingIsAbsentRatherThanEmpty()
    {
        // Absent and mapped-to-empty mean the same thing to every reader, so the map keeps only what
        // actually constrains something.
        var graph = Build(
            new TaskWaitPolicy(WaitForSubtasks: false, WaitForLinked: false),
            Task(Parent, subtasks: [SubA], linked: [LinkA]));

        Assert.False(graph.ContainsKey(Parent));
    }

    [Fact]
    public void APrerequisiteStatedByBothSourcesIsOneEdge()
    {
        // A tracker can declare a dependency the hierarchy already implies.
        var graph = Build(TaskWaitPolicy.Default, Task(Parent, subtasks: [SubA], linked: [SubA]));

        Assert.Equal([SubA], graph[Parent]);
    }

    [Fact]
    public void ATaskIsNeverItsOwnPrerequisite()
    {
        var graph = Build(TaskWaitPolicy.Default, Task(Parent, subtasks: [Parent], linked: [LinkA]));

        Assert.Equal([LinkA], graph[Parent]);
    }

    [Fact]
    public void SubtasksFirstMakesEveryLinkedTaskWaitForEverySubtask()
    {
        var graph = Build(
            TaskWaitPolicy.Default with { GroupOrder = PrerequisiteGroupOrder.SubtasksFirst },
            Task(Parent, subtasks: [SubA, SubB], linked: [LinkA]));

        Assert.Equal([SubA, SubB], graph[LinkA]);
    }

    [Fact]
    public void LinkedFirstMakesEverySubtaskWaitForEveryLinkedTask()
    {
        var graph = Build(
            TaskWaitPolicy.Default with { GroupOrder = PrerequisiteGroupOrder.LinkedFirst },
            Task(Parent, subtasks: [SubA], linked: [LinkA, LinkB]));

        Assert.Equal([LinkA, LinkB], graph[SubA]);
    }

    [Fact]
    public void TogetherImposesNothingBetweenTheGroups()
    {
        // The default, and deliberately so: nothing says a subtask must precede a linked task, and
        // inventing that edge is what can turn an acyclic graph cyclic.
        var graph = Build(TaskWaitPolicy.Default, Task(Parent, subtasks: [SubA], linked: [LinkA]));

        Assert.False(graph.ContainsKey(SubA));
        Assert.False(graph.ContainsKey(LinkA));
    }

    [Fact]
    public void AGroupOrderIsNotImposedWhenOneGroupIsEmpty()
    {
        var graph = Build(
            TaskWaitPolicy.Default with { GroupOrder = PrerequisiteGroupOrder.SubtasksFirst },
            Task(Parent, subtasks: [SubA, SubB]));

        Assert.False(graph.ContainsKey(SubA));
        Assert.False(graph.ContainsKey(SubB));
    }

    [Fact]
    public void AManualSequenceChainsThePrerequisitesInOrder()
    {
        var graph = Build(
            TaskWaitPolicy.Default,
            Task(Parent, subtasks: [SubA, SubB], linked: [LinkA], manualOrder: [LinkA, SubA, SubB]));

        // Chained pairwise: "after" is transitive through the graph, so consecutive edges suffice.
        Assert.Equal([LinkA], graph[SubA]);
        Assert.Equal([SubA], graph[SubB]);
    }

    [Fact]
    public void AManualEntryThePolicyExcludedIsIgnored()
    {
        // A stale sequence naming a subtask must not resurrect it once subtasks stopped being waited
        // on — the sequence orders the prerequisites that exist, it does not create them.
        var graph = Build(
            TaskWaitPolicy.Default with { WaitForSubtasks = false },
            Task(Parent, subtasks: [SubA], linked: [LinkA, LinkB], manualOrder: [SubA, LinkA, LinkB]));

        Assert.False(graph.ContainsKey(SubA));
        Assert.Equal([LinkA], graph[LinkB]);
    }

    [Fact]
    public void PolicyIsResolvedPerTask()
    {
        // The per-task override is the point: one task may decline its subtasks while another keeps them.
        var graph = TaskWaitGraph.Build(
            [Task(Parent, subtasks: [SubA]), Task(LinkA, subtasks: [SubB])],
            id => id == Parent ? TaskWaitPolicy.Default with { WaitForSubtasks = false } : TaskWaitPolicy.Default);

        Assert.False(graph.ContainsKey(Parent));
        Assert.Equal([SubB], graph[LinkA]);
    }

    [Fact]
    public void OverriddenByTakesTheTasksAnswerAndInheritsTheRest()
    {
        var global = new TaskWaitPolicy(WaitForSubtasks: true, WaitForLinked: true, PrerequisiteGroupOrder.SubtasksFirst);

        var effective = global.OverriddenBy(waitForSubtasks: false, waitForLinked: null, groupOrder: null);

        Assert.False(effective.WaitForSubtasks);
        Assert.True(effective.WaitForLinked);
        Assert.Equal(PrerequisiteGroupOrder.SubtasksFirst, effective.GroupOrder);
    }
}
