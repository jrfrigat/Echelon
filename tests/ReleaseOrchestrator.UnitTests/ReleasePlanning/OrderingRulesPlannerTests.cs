using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using ReleaseOrchestrator.Core.Enums;
using ReleaseOrchestrator.Infrastructure.Persistence.Models;
using ReleaseOrchestrator.Infrastructure.ReleasePlanning;
using Xunit;

namespace ReleaseOrchestrator.UnitTests.ReleasePlanning;

/// <summary>
/// An ordering-rule document actually changing the deploy order, end to end through the planner.
/// </summary>
/// <remarks>
/// These exist because the same failure already happened once. The wait policy had tests over its
/// pure function and none over the wiring, and it turned out to reach the closure and not the edges:
/// the policy decided which tasks a plan covered and left the deploy order byte-identical. Nothing
/// failed and nothing was logged. A document is in exactly that position — thoroughly tested as a
/// parser and a compiler — so the wiring gets its own tests rather than the benefit of the doubt.
/// </remarks>
public class OrderingRulesPlannerTests : PlannerTestBase
{
    private RolloutPlanner Planner() =>
        new(Db, new FakeTimeProvider(Now), NullLogger<RolloutPlanner>.Instance);

    private void SaveDocument(string yaml) =>
        Db.PlanningSettings.Add(new PlanningSettings
        {
            Id = PlanningSettings.SingletonId,
            OrderingRulesDocument = yaml
        });

    /// <summary>The wave a merge request landed in, by its external id.</summary>
    private static Dictionary<string, int> WavesByMr(
        Application.DTOs.RolloutPlanDto plan) =>
        plan.Nodes
            .SelectMany(n => n.Items)
            .ToDictionary(i => i.MrExternalId, i => i.Wave);

    [Fact]
    public async Task ADocumentOrdersMergeRequestsThatNothingElseOrders()
    {
        // One task, two repositories, no repository-dependency rows: without a document these deploy
        // together, so any separation is the document's doing and nothing else's.
        var db = AddRepository("db");
        var web = AddRepository("web");
        var task = AddTask("PROJ-1");
        AddMergeRequest(db, task, externalId: "db-1");
        AddMergeRequest(web, task, externalId: "web-1");

        SaveDocument("""
            version: 1
            groups:
              backend:  { repositories: ["group/db"] }
              frontend: { repositories: ["group/web"] }
            order:
              - group: frontend
                needs: [backend]
                type: hard
            """);
        await Db.SaveChangesAsync(Ct);

        var waves = WavesByMr(await Planner().RecalculateAsync(task.Id, actor: null, Ct));

        Assert.True(waves["db-1"] < waves["web-1"], $"db-1 was wave {waves["db-1"]}, web-1 was {waves["web-1"]}");
    }

    [Fact]
    public async Task WithoutADocumentTheSameWorkDeploysTogether()
    {
        // The control for the test above: proves the separation came from the document rather than
        // from something else in the planner that would have ordered them anyway.
        var db = AddRepository("db");
        var web = AddRepository("web");
        var task = AddTask("PROJ-1");
        AddMergeRequest(db, task, externalId: "db-1");
        AddMergeRequest(web, task, externalId: "web-1");
        await Db.SaveChangesAsync(Ct);

        var waves = WavesByMr(await Planner().RecalculateAsync(task.Id, actor: null, Ct));

        Assert.Equal(waves["db-1"], waves["web-1"]);
    }

    [Fact]
    public async Task WithinTaskKeepsUnrelatedTasksParallel()
    {
        // The scope that the worked example in 012 made necessary. Two tasks under one umbrella, each
        // with a backend and a frontend repository; across the plan the rule would chain them.
        var (target, _) = BuildTwoIndependentSubtasks();

        SaveDocument("""
            version: 1
            groups:
              backend:  { repositories: ["group/db", "group/api"] }
              frontend: { repositories: ["group/web", "group/reports"] }
            order:
              - group: frontend
                needs: [backend]
                type: hard
                scope: within_task
            """);
        await Db.SaveChangesAsync(Ct);

        var waves = WavesByMr(await Planner().RecalculateAsync(target.Id, actor: null, Ct));

        // Each task's frontend waits for its own backend...
        Assert.True(waves["web-2"] > waves["db-2"]);
        Assert.True(waves["reports-3"] > waves["api-3"]);

        // ...and the two tasks stay abreast rather than being chained into one sequence.
        Assert.Equal(waves["db-2"], waves["api-3"]);
        Assert.Equal(waves["web-2"], waves["reports-3"]);
    }

    [Fact]
    public async Task AcrossPlanChainsThemInstead()
    {
        // The same document with the default scope, to show the difference is the scope and not the
        // selectors: now one task's frontend sits behind the other task's backend.
        var (target, _) = BuildTwoIndependentSubtasks();

        SaveDocument("""
            version: 1
            groups:
              backend:  { repositories: ["group/db", "group/api"] }
              frontend: { repositories: ["group/web", "group/reports"] }
            order:
              - group: frontend
                needs: [backend]
                type: hard
            """);
        await Db.SaveChangesAsync(Ct);

        var waves = WavesByMr(await Planner().RecalculateAsync(target.Id, actor: null, Ct));

        // Every frontend now waits for every backend, so both frontends land after both backends.
        Assert.True(waves["web-2"] > waves["api-3"]);
        Assert.True(waves["reports-3"] > waves["db-2"]);
    }

    [Fact]
    public async Task ADocumentThatContradictsAHardRuleIsReportedRatherThanApplied()
    {
        // A soft rule against a hard repository dependency: the cycle is broken by dropping the soft
        // edge, and the plan says so. A plan may deploy against a constraint, never silently.
        var db = AddRepository("db");
        var web = AddRepository("web");
        var task = AddTask("PROJ-1");
        AddMergeRequest(db, task, externalId: "db-1");
        AddMergeRequest(web, task, externalId: "web-1");
        AddRepositoryDependency(web, db, StackDependencyType.Hard);

        SaveDocument("""
            version: 1
            groups:
              backend:  { repositories: ["group/db"] }
              frontend: { repositories: ["group/web"] }
            order:
              - group: backend
                needs: [frontend]
                type: soft
            """);
        await Db.SaveChangesAsync(Ct);

        var plan = await Planner().RecalculateAsync(task.Id, actor: null, Ct);

        Assert.NotEmpty(plan.Conflicts);
        // The hard constraint survived; the soft one from the document is what yielded.
        var waves = WavesByMr(plan);
        Assert.True(waves["db-1"] < waves["web-1"]);
    }

    [Fact]
    public async Task AnUnreadableDocumentLeavesTheDerivedOrderStandingRatherThanFailingThePlan()
    {
        // One bad edit must not take out every plan in the installation, including the plans of people
        // who did not make it. The PUT refuses to store an invalid document; this is the backstop.
        var db = AddRepository("db");
        var web = AddRepository("web");
        var task = AddTask("PROJ-1");
        AddMergeRequest(db, task, externalId: "db-1");
        AddMergeRequest(web, task, externalId: "web-1");
        AddRepositoryDependency(web, db, StackDependencyType.Hard);

        SaveDocument("version: 1\ngroups:\n  a: { repositores: [\"x\"] }\n");
        await Db.SaveChangesAsync(Ct);

        var waves = WavesByMr(await Planner().RecalculateAsync(task.Id, actor: null, Ct));

        // The repository dependency still ordered them.
        Assert.True(waves["db-1"] < waves["web-1"]);
    }

    /// <summary>
    /// An umbrella over two subtasks that have nothing to do with each other, each touching a backend
    /// and a frontend repository.
    /// </summary>
    private (TaskItem Target, TaskItem Task2) BuildTwoIndependentSubtasks()
    {
        var task1 = AddTask("TASK-1");
        var task2 = AddTask("TASK-2");
        var task3 = AddTask("TASK-3");
        AddChild(task1, task2);
        AddChild(task1, task3);

        AddMergeRequest(AddRepository("db"), task2, externalId: "db-2");
        AddMergeRequest(AddRepository("web"), task2, externalId: "web-2");
        AddMergeRequest(AddRepository("api"), task3, externalId: "api-3");
        AddMergeRequest(AddRepository("reports"), task3, externalId: "reports-3");

        return (task1, task2);
    }
}
