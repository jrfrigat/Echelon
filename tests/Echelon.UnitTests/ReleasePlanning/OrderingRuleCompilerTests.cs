using Echelon.Application.ReleasePlanning;
using Echelon.Core.Enums;
using Xunit;

namespace Echelon.UnitTests.ReleasePlanning;

/// <summary>
/// What an ordering rule document means, pinned as behaviour rather than as prose.
/// </summary>
/// <remarks>
/// The format is being approved before an editor is built, so these are the approval: the schema is a
/// mapping onto <c>OrderingRules</c>, and what the document DOES is decided here. A syntax change is
/// cheap later; a semantics change after plans depend on it is not.
/// </remarks>
public class OrderingRuleCompilerTests
{
    // The worked example from docs/issues/012: task-1 is an umbrella over task-2 and task-3, which are
    // unrelated to each other, and task-2 additionally waits on task-4.
    static readonly Guid Task2 = new("22222222-0000-0000-0000-000000000000");
    static readonly Guid Task3 = new("33333333-0000-0000-0000-000000000000");
    static readonly Guid Task4 = new("44444444-0000-0000-0000-000000000000");

    static readonly OrderingCandidate SvcDb =
        Mr("2a", Task2, "gitlab-main", "group/svc-db", "feature/task-2-db");
    static readonly OrderingCandidate PartnerWeb =
        Mr("2b", Task2, "gitlab-partner", "partner/web", "feature/task-2-web");
    static readonly OrderingCandidate SvcApi =
        Mr("3a", Task3, "gitlab-main", "group/svc-api", "feature/task-3-api");
    static readonly OrderingCandidate PartnerReports =
        Mr("3b", Task3, "gitlab-partner", "partner/reports", "feature/task-3-reports");
    static readonly OrderingCandidate SvcAuth =
        Mr("4a", Task4, "gitlab-main", "group/svc-auth", "feature/task-4-auth");

    static readonly OrderingCandidate[] All = [SvcDb, PartnerWeb, SvcApi, PartnerReports, SvcAuth];

    static OrderingCandidate Mr(
        string id, Guid task, string connector, string repository, string branch, params string[] labels) =>
        new(new Guid($"{id[0]}{id[0]}{id[0]}{id[0]}{id[1]}{id[1]}{id[1]}{id[1]}-1111-1111-1111-111111111111"),
            task, connector, repository, branch, $"TASK-{id}", labels);

    static readonly Dictionary<string, WorkSelector> NoGroups = [];

    /// <summary>The edges, for the cases that are about ordering rather than about the cap.</summary>
    static IReadOnlyList<OrderingEdge> Edges(OrderingRules rules, IReadOnlyList<OrderingCandidate> candidates)
    {
        var result = OrderingRuleCompiler.Compile(rules, candidates);
        Assert.False(result.LimitExceeded, $"unexpectedly hit the edge cap on group '{result.ExceededOn}'");
        return result.Edges;
    }

    static OrderingRules Rules(
        IReadOnlyDictionary<string, WorkSelector> groups,
        IReadOnlyList<GroupOrderSpec> order,
        TaskPolicySpec? tasks = null) =>
        new(1, tasks ?? new TaskPolicySpec(null, null, null, []), groups, order);

    static WorkSelector Select(
        string[]? connectors = null, string[]? repositories = null, string[]? branches = null,
        string[]? taskKeys = null, string[]? labels = null, WorkSelector? exclude = null) =>
        new(connectors ?? [], repositories ?? [], branches ?? [], taskKeys ?? [], labels ?? [], exclude);

    // ---- selectors ------------------------------------------------------------------------

    [Fact]
    public void AGlobOverRepositoriesSelectsTheMatchingOnes()
    {
        var selector = Select(repositories: ["group/svc-*"]);

        Assert.True(selector.Matches(SvcDb));
        Assert.True(selector.Matches(SvcApi));
        Assert.False(selector.Matches(PartnerWeb));
    }

    [Fact]
    public void AxesCombineWithAnd()
    {
        // Both must hold: the partner connector AND a group/* repository matches nothing here.
        var selector = Select(connectors: ["gitlab-partner"], repositories: ["group/*"]);

        Assert.DoesNotContain(All, selector.Matches);
    }

    [Fact]
    public void ValuesWithinAnAxisCombineWithOr()
    {
        var selector = Select(repositories: ["group/svc-db", "partner/web"]);

        Assert.True(selector.Matches(SvcDb));
        Assert.True(selector.Matches(PartnerWeb));
        Assert.False(selector.Matches(SvcApi));
    }

    [Fact]
    public void ExcludeSubtracts()
    {
        var selector = Select(repositories: ["group/svc-*"], exclude: Select(repositories: ["group/svc-db"]));

        Assert.False(selector.Matches(SvcDb));
        Assert.True(selector.Matches(SvcApi));
    }

    [Fact]
    public void LabelsMatchExactlyRatherThanByGlob()
    {
        // A label is an identifier; a glob over it would quietly widen whatever it gates.
        var urgent = Mr("9a", Task2, "gitlab-main", "group/svc-db", "b", "urgent");

        Assert.True(Select(labels: ["urgent"]).Matches(urgent));
        Assert.False(Select(labels: ["urg*"]).Matches(urgent));
    }

    [Fact]
    public void AnUnsetAxisConstrainsNothing()
        => Assert.All(All, c => Assert.True(WorkSelector.Any.Matches(c)));

    // ---- ordering -------------------------------------------------------------------------

    [Fact]
    public void NeedsMakesEveryMemberOfTheGroupWaitForEveryMemberOfTheNeededGroup()
    {
        var rules = Rules(
            new Dictionary<string, WorkSelector>
            {
                ["backend"] = Select(repositories: ["group/svc-*"]),
                ["frontend"] = Select(repositories: ["partner/*"])
            },
            [new GroupOrderSpec("frontend", ["backend"], StackDependencyType.Hard)]);

        var edges = Edges(rules, All);

        // 3 backend x 2 frontend, every pair.
        Assert.Equal(6, edges.Count);
        Assert.Contains(edges, e => e.From == SvcDb.MergeRequestId && e.To == PartnerWeb.MergeRequestId);
        Assert.Contains(edges, e => e.From == SvcAuth.MergeRequestId && e.To == PartnerReports.MergeRequestId);
    }

    [Fact]
    public void WithinTaskKeepsUnrelatedTasksParallel()
    {
        // The reason the scope exists. task-2 and task-3 share only a parent, so ordering one's
        // frontend behind the other's backend is a correct plan that has quietly lost parallelism.
        var rules = Rules(
            new Dictionary<string, WorkSelector>
            {
                ["backend"] = Select(connectors: ["gitlab-main"]),
                ["frontend"] = Select(connectors: ["gitlab-partner"])
            },
            [new GroupOrderSpec("frontend", ["backend"], StackDependencyType.Hard, OrderScope.WithinTask)]);

        var edges = Edges(rules, All);

        // Only the same-task pairs survive.
        Assert.Equal(2, edges.Count);
        Assert.Contains(edges, e => e.From == SvcDb.MergeRequestId && e.To == PartnerWeb.MergeRequestId);
        Assert.Contains(edges, e => e.From == SvcApi.MergeRequestId && e.To == PartnerReports.MergeRequestId);

        // And crucially not these, which are what AcrossPlan would have added.
        Assert.DoesNotContain(edges, e => e.From == SvcApi.MergeRequestId && e.To == PartnerWeb.MergeRequestId);
        Assert.DoesNotContain(edges, e => e.From == SvcAuth.MergeRequestId && e.To == PartnerReports.MergeRequestId);
    }

    [Fact]
    public void AcrossPlanIsTheDefaultSoExistingRulesKeepTheirMeaning()
    {
        var rules = Rules(
            new Dictionary<string, WorkSelector>
            {
                ["backend"] = Select(connectors: ["gitlab-main"]),
                ["frontend"] = Select(connectors: ["gitlab-partner"])
            },
            [new GroupOrderSpec("frontend", ["backend"])]);

        var edges = Edges(rules, All);

        Assert.Contains(edges, e => e.From == SvcApi.MergeRequestId && e.To == PartnerWeb.MergeRequestId);
    }

    [Fact]
    public void OverlappingGroupsProduceNoSelfEdge()
    {
        // Ordinary: "everything on this connector" and "partner/*" can match the same merge request.
        var rules = Rules(
            new Dictionary<string, WorkSelector>
            {
                ["a"] = Select(repositories: ["partner/*"]),
                ["b"] = Select(connectors: ["gitlab-partner"])
            },
            [new GroupOrderSpec("a", ["b"])]);

        var edges = Edges(rules, All);

        Assert.DoesNotContain(edges, e => e.From == e.To);
    }

    [Fact]
    public void AnEmptyGroupSimplyAddsNoEdges()
    {
        // A repository configured but with no work in this plan is a normal state, not an error.
        var rules = Rules(
            new Dictionary<string, WorkSelector>
            {
                ["absent"] = Select(repositories: ["group/nothing-here"]),
                ["frontend"] = Select(repositories: ["partner/*"])
            },
            [new GroupOrderSpec("frontend", ["absent"])]);

        Assert.Empty(Edges(rules, All));
    }

    [Fact]
    public void EdgesAreDeduplicatedAcrossRules()
    {
        // Two rules can imply the same pair; the planner must see it once.
        var rules = Rules(
            new Dictionary<string, WorkSelector>
            {
                ["db"] = Select(repositories: ["group/svc-db"]),
                ["web"] = Select(repositories: ["partner/web"])
            },
            [
                new GroupOrderSpec("web", ["db"]),
                new GroupOrderSpec("web", ["db"], StackDependencyType.Soft)
            ]);

        var edge = Assert.Single(Edges(rules, All));
        // The first rule wins, so the firmer statement is not silently downgraded by a later duplicate.
        Assert.Equal(StackDependencyType.Hard, edge.Type);
    }

    // ---- the worked example ---------------------------------------------------------------

    [Fact]
    public void TheWorkedExampleOrdersEachTasksFrontendBehindItsOwnBackend()
    {
        var rules = Rules(
            new Dictionary<string, WorkSelector>
            {
                ["backend-main"] = Select(
                    connectors: ["gitlab-main"], repositories: ["group/svc-db", "group/svc-api"]),
                ["frontend-partner"] = Select(connectors: ["gitlab-partner"], repositories: ["partner/*"])
            },
            [new GroupOrderSpec("frontend-partner", ["backend-main"], StackDependencyType.Hard, OrderScope.WithinTask)]);

        var edges = Edges(rules, All);

        Assert.Equal(2, edges.Count);
        Assert.Contains(edges, e => e.From == SvcDb.MergeRequestId && e.To == PartnerWeb.MergeRequestId);
        Assert.Contains(edges, e => e.From == SvcApi.MergeRequestId && e.To == PartnerReports.MergeRequestId);

        // svc-auth is excluded from backend-main by the repository axis, so task-4 is ordered by its
        // task link alone - which is the point: the document adds repository ordering and does not
        // restate what the tracker already said.
        Assert.DoesNotContain(edges, e => e.From == SvcAuth.MergeRequestId);
    }

    // ---- the edge cap ---------------------------------------------------------------------

    [Fact]
    public void ADocumentThatExpandsPastTheCapIsRefusedAndNamesTheRule()
    {
        // 'needs' is a cross product and nothing in the syntax hints at it: two groups of 100 is
        // 10,000 edges from one line. Refused whole rather than truncated -- a partial cross product
        // is an ordering nobody wrote, constraining some pairs and not others, arbitrarily.
        var many = Enumerable.Range(0, 100)
            .SelectMany(i => new[]
            {
                Mr($"a{i % 10}", Task2, "gitlab-main", $"group/back-{i}", $"b{i}") with
                    { MergeRequestId = Guid.NewGuid() },
                Mr($"b{i % 10}", Task3, "gitlab-main", $"group/front-{i}", $"f{i}") with
                    { MergeRequestId = Guid.NewGuid() }
            })
            .ToList();

        var rules = Rules(
            new Dictionary<string, WorkSelector>
            {
                ["backend"] = Select(repositories: ["group/back-*"]),
                ["frontend"] = Select(repositories: ["group/front-*"])
            },
            [new GroupOrderSpec("frontend", ["backend"])]);

        var result = OrderingRuleCompiler.Compile(rules, many);

        Assert.True(result.LimitExceeded);
        Assert.Equal("frontend", result.ExceededOn);
        Assert.True(result.Edges.Count <= OrderingRuleCompiler.MaxEdges);
    }

    [Fact]
    public void ADocumentThatFitsReportsNoLimit()
    {
        var rules = Rules(
            new Dictionary<string, WorkSelector>
            {
                ["backend"] = Select(repositories: ["group/svc-*"]),
                ["frontend"] = Select(repositories: ["partner/*"])
            },
            [new GroupOrderSpec("frontend", ["backend"])]);

        var result = OrderingRuleCompiler.Compile(rules, All);

        Assert.False(result.LimitExceeded);
        Assert.Null(result.ExceededOn);
    }

    // ---- task policy ----------------------------------------------------------------------

    [Fact]
    public void TheDocumentOverridesTheStoredDefault()
    {
        var rules = Rules(NoGroups, [], new TaskPolicySpec(
            WaitForSubtasks: false, WaitForLinked: null, GroupOrder: null, Overrides: []));

        var policy = OrderingRuleCompiler.ResolvePolicy(rules, TaskWaitPolicy.Default, SvcDb);

        Assert.False(policy.WaitForSubtasks);
        Assert.True(policy.WaitForLinked);
    }

    [Fact]
    public void ANullInTheDocumentLeavesTheStoredDefaultAlone()
    {
        var stored = new TaskWaitPolicy(WaitForSubtasks: false, WaitForLinked: true);

        var policy = OrderingRuleCompiler.ResolvePolicy(Rules(NoGroups, []), stored, SvcDb);

        Assert.False(policy.WaitForSubtasks);
    }

    [Fact]
    public void APerTaskOverrideAppliesToTheTasksItSelects()
    {
        var rules = Rules(NoGroups, [], new TaskPolicySpec(null, null, null, [
            new TaskPolicyOverride(Select(repositories: ["group/svc-db"]), WaitForSubtasks: false, null, null)
        ]));

        Assert.False(OrderingRuleCompiler.ResolvePolicy(rules, TaskWaitPolicy.Default, SvcDb).WaitForSubtasks);
        Assert.True(OrderingRuleCompiler.ResolvePolicy(rules, TaskWaitPolicy.Default, SvcApi).WaitForSubtasks);
    }

    [Fact]
    public void TheFirstMatchingOverrideWins()
    {
        // Scanning down and stopping is the only resolution order that stays obvious as the list grows.
        var rules = Rules(NoGroups, [], new TaskPolicySpec(null, null, null, [
            new TaskPolicyOverride(Select(repositories: ["group/*"]), WaitForSubtasks: false, null, null),
            new TaskPolicyOverride(Select(repositories: ["group/svc-db"]), WaitForSubtasks: true, null, null)
        ]));

        Assert.False(OrderingRuleCompiler.ResolvePolicy(rules, TaskWaitPolicy.Default, SvcDb).WaitForSubtasks);
    }
}
