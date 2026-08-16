using Echelon.Application.ReleasePlanning;
using Echelon.Core.Enums;
using Xunit;

namespace Echelon.UnitTests.ReleasePlanning;

/// <summary>
/// Writing the ordering-rule document back out, so it can be built by clicking.
/// </summary>
/// <remarks>
/// Every test here is a round trip through the REAL reader rather than a string comparison. What
/// matters is not the exact text - that is the serializer's business - but that the reader
/// understands it to mean what was written. A writer checked against expected strings passes while
/// producing a document the planner reads differently, which is the one failure that would matter.
/// </remarks>
public class OrderingRuleWriterTests
{
    private static OrderingRules RoundTrip(OrderingRules rules)
    {
        var parsed = OrderingRuleDocument.Read(OrderingRuleWriter.Write(rules));
        Assert.True(parsed.IsValid, string.Join("; ", parsed.Errors));
        return parsed.Rules!;
    }

    private static OrderingRules Rules(
        Dictionary<string, WorkSelector>? groups = null,
        IReadOnlyList<GroupOrderSpec>? order = null,
        TaskPolicySpec? tasks = null) =>
        new(1, tasks ?? new TaskPolicySpec(null, null, null, []), groups ?? [], order ?? []);

    private static WorkSelector Selector(
        IReadOnlyList<string>? repositories = null,
        IReadOnlyList<string>? connectors = null,
        IReadOnlyList<string>? branches = null,
        IReadOnlyList<string>? taskKeys = null,
        IReadOnlyList<string>? labels = null,
        WorkSelector? exclude = null) =>
        new(connectors ?? [], repositories ?? [], branches ?? [], taskKeys ?? [], labels ?? [], exclude);

    [Fact]
    public void AnEmptyDocumentRoundTrips()
    {
        var result = RoundTrip(OrderingRules.Empty);

        Assert.Empty(result.Groups);
        Assert.Empty(result.Order);
    }

    [Fact]
    public void GroupsAndOrderRoundTrip()
    {
        var rules = Rules(
            groups: new()
            {
                ["backend"] = Selector(repositories: ["group/api", "group/worker"]),
                ["frontend"] = Selector(repositories: ["group/web"])
            },
            order: [new GroupOrderSpec("frontend", ["backend"])]);

        var result = RoundTrip(rules);

        Assert.Equal(["backend", "frontend"], result.Groups.Keys.OrderBy(k => k, StringComparer.Ordinal));
        Assert.Equal(["group/api", "group/worker"], result.Groups["backend"].Repositories);

        var spec = Assert.Single(result.Order);
        Assert.Equal("frontend", spec.Group);
        Assert.Equal(["backend"], spec.Needs);
    }

    /// <summary>Every selector axis survives, including the ones a form is least likely to exercise.</summary>
    [Fact]
    public void EverySelectorAxisRoundTrips()
    {
        var rules = Rules(
            groups: new()
            {
                ["everything"] = Selector(
                    repositories: ["group/*"],
                    connectors: ["gitlab-*"],
                    branches: ["release/*"],
                    taskKeys: ["PROJ-*"],
                    labels: ["ready"])
            },
            order: []);

        var group = RoundTrip(rules).Groups["everything"];

        Assert.Equal(["gitlab-*"], group.Connectors);
        Assert.Equal(["group/*"], group.Repositories);
        Assert.Equal(["release/*"], group.Branches);
        Assert.Equal(["PROJ-*"], group.TaskKeys);
        Assert.Equal(["ready"], group.Labels);
    }

    [Fact]
    public void SoftAndWithinTaskRoundTrip()
    {
        var rules = Rules(
            groups: new() { ["a"] = Selector(repositories: ["x"]), ["b"] = Selector(repositories: ["y"]) },
            order: [new GroupOrderSpec("a", ["b"], StackDependencyType.Soft, OrderScope.WithinTask)]);

        var spec = Assert.Single(RoundTrip(rules).Order);

        Assert.Equal(StackDependencyType.Soft, spec.Type);
        Assert.Equal(OrderScope.WithinTask, spec.Scope);
    }

    /// <summary>
    /// Defaults are not written, and still read back as themselves.
    /// </summary>
    /// <remarks>
    /// A generated document has to read like a hand-written one, or nobody will edit it by hand
    /// again - and hand editing must stay possible, because the language says things no form will.
    /// </remarks>
    [Fact]
    public void DefaultsAreOmittedFromTheText()
    {
        var rules = Rules(
            groups: new() { ["a"] = Selector(repositories: ["x"]), ["b"] = Selector(repositories: ["y"]) },
            order: [new GroupOrderSpec("a", ["b"], StackDependencyType.Hard, OrderScope.AcrossPlan)]);

        var text = OrderingRuleWriter.Write(rules);

        Assert.DoesNotContain("scope:", text, StringComparison.Ordinal);
        Assert.DoesNotContain("type:", text, StringComparison.Ordinal);

        var spec = Assert.Single(RoundTrip(rules).Order);
        Assert.Equal(StackDependencyType.Hard, spec.Type);
        Assert.Equal(OrderScope.AcrossPlan, spec.Scope);
    }

    /// <summary>An empty axis is absent, not written as an empty list nobody meant.</summary>
    [Fact]
    public void UnsetAxesAreNotWritten()
    {
        var text = OrderingRuleWriter.Write(Rules(
            groups: new() { ["a"] = Selector(repositories: ["x"]) }));

        Assert.DoesNotContain("labels", text, StringComparison.Ordinal);
        Assert.DoesNotContain("branches", text, StringComparison.Ordinal);
        Assert.DoesNotContain("connectors", text, StringComparison.Ordinal);
    }

    /// <summary>The wait policy and its per-task exceptions survive, though no form edits them.</summary>
    [Fact]
    public void TaskPolicyRoundTrips()
    {
        var rules = Rules(
            groups: new() { ["a"] = Selector(repositories: ["x"]) },
            tasks: new TaskPolicySpec(
                WaitForSubtasks: false,
                WaitForLinked: true,
                GroupOrder: PrerequisiteGroupOrder.SubtasksFirst,
                Overrides: [new TaskPolicyOverride(Selector(taskKeys: ["HOTFIX-*"]), false, false, null)]));

        var result = RoundTrip(rules).Tasks;

        Assert.False(result.WaitForSubtasks);
        Assert.True(result.WaitForLinked);
        Assert.Equal(PrerequisiteGroupOrder.SubtasksFirst, result.GroupOrder);

        var exception = Assert.Single(result.Overrides);
        Assert.Equal(["HOTFIX-*"], exception.Match.TaskKeys);
        Assert.False(exception.WaitForSubtasks);
    }

    /// <summary>A nested exclude survives, which is what makes the writer safe for any stored document.</summary>
    [Fact]
    public void NestedExcludeRoundTrips()
    {
        var rules = Rules(
            groups: new()
            {
                ["most"] = Selector(repositories: ["group/*"], exclude: Selector(repositories: ["group/legacy"]))
            });

        var group = RoundTrip(rules).Groups["most"];

        Assert.NotNull(group.Exclude);
        Assert.Equal(["group/legacy"], group.Exclude.Repositories);
    }

    /// <summary>
    /// Text that would break the document if concatenated is quoted by the serializer.
    /// </summary>
    /// <remarks>
    /// Group names and globs are operator input. A group called <c>a: b</c> or a glob starting with
    /// <c>*</c> is exactly where hand-rolled string building produces a file that parses into
    /// something else entirely.
    /// </remarks>
    [Fact]
    public void OperatorTextThatNeedsQuotingRoundTrips()
    {
        var rules = Rules(
            groups: new() { ["odd: name #1"] = Selector(repositories: ["*/api", "group/x:y"]) },
            order: []);

        var group = Assert.Single(RoundTrip(rules).Groups);

        Assert.Equal("odd: name #1", group.Key);
        Assert.Equal(["*/api", "group/x:y"], group.Value.Repositories);
    }
}
