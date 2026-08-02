using ReleaseOrchestrator.Application.ReleasePlanning;
using ReleaseOrchestrator.Core.Enums;
using Xunit;

namespace ReleaseOrchestrator.UnitTests.ReleasePlanning;

/// <summary>
/// Reading an ordering-rule document, and refusing to read a wrong one quietly.
/// </summary>
/// <remarks>
/// The document decides deploy order, so the expensive failure is not a rejected file — it is an
/// accepted one that means something other than what was written. Most of these are about that:
/// unknown keys, undefined groups and unusable values are errors, never skipped.
/// </remarks>
public class OrderingRuleDocumentTests
{
    static OrderingRules ReadValid(string json)
    {
        var result = OrderingRuleDocument.Read(json);
        Assert.True(result.IsValid, $"expected a valid document, got: {string.Join(" | ", result.Errors)}");
        return result.Rules!;
    }

    static IReadOnlyList<string> ReadErrors(string json)
    {
        var result = OrderingRuleDocument.Read(json);
        Assert.False(result.IsValid);
        return result.Errors;
    }

    [Fact]
    public void AnEmptyDocumentMeansNoRulesRatherThanAnError()
    {
        // What an installation that configured nothing plans by; it must not be a failure.
        foreach (var text in new[] { null, "", "   " })
        {
            var result = OrderingRuleDocument.Read(text);
            Assert.True(result.IsValid);
            Assert.Empty(result.Rules!.Order);
        }
    }

    [Fact]
    public void ReadsTheWorkedExample()
    {
        var rules = ReadValid("""
        {
          "version": 1,
          "tasks": { "wait_for_subtasks": true, "wait_for_linked": true, "group_order": "together" },
          "groups": {
            "backend-main":     { "connectors": ["gitlab-main"], "repositories": ["group/svc-db", "group/svc-api"] },
            "frontend-partner": { "connectors": ["gitlab-partner"], "repositories": ["partner/*"] }
          },
          "order": [
            { "group": "frontend-partner", "needs": ["backend-main"], "type": "hard", "scope": "within_task" }
          ]
        }
        """);

        Assert.Equal(1, rules.Version);
        Assert.Equal(2, rules.Groups.Count);
        Assert.Equal(["group/svc-db", "group/svc-api"], rules.Groups["backend-main"].Repositories);

        var rule = Assert.Single(rules.Order);
        Assert.Equal("frontend-partner", rule.Group);
        Assert.Equal(["backend-main"], rule.Needs);
        Assert.Equal(StackDependencyType.Hard, rule.Type);
        Assert.Equal(OrderScope.WithinTask, rule.Scope);

        Assert.True(rules.Tasks.WaitForSubtasks);
        Assert.Equal(PrerequisiteGroupOrder.Together, rules.Tasks.GroupOrder);
    }

    [Fact]
    public void OmittedPolicyFieldsStayNullSoTheStoredDefaultIsLeftAlone()
    {
        var rules = ReadValid("""{ "version": 1, "tasks": { "wait_for_subtasks": false } }""");

        Assert.False(rules.Tasks.WaitForSubtasks);
        Assert.Null(rules.Tasks.WaitForLinked);
        Assert.Null(rules.Tasks.GroupOrder);
    }

    [Fact]
    public void DefaultsTypeToHardAndScopeToAcrossPlan()
    {
        // AcrossPlan is what repository ordering already does, so an existing rule rewritten in this
        // language keeps its meaning rather than quietly narrowing.
        var rules = ReadValid("""
        { "version": 1, "groups": { "a": {}, "b": {} }, "order": [ { "group": "a", "needs": ["b"] } ] }
        """);

        var rule = Assert.Single(rules.Order);
        Assert.Equal(StackDependencyType.Hard, rule.Type);
        Assert.Equal(OrderScope.AcrossPlan, rule.Scope);
    }

    [Fact]
    public void AnUnknownKeyIsAnError()
    {
        // The whole point: a mistyped key that silently selects nothing is the failure this document
        // cannot afford, because it surfaces as the deploy order being wrong.
        var errors = ReadErrors("""
        { "version": 1, "groups": { "a": { "repositores": ["x"] } } }
        """);

        Assert.Contains(errors, e => e.Contains("repositores") && e.Contains("Valid keys"));
    }

    [Fact]
    public void AnUndefinedGroupIsNamedAlongWithWhatWasAvailable()
    {
        var errors = ReadErrors("""
        { "version": 1, "groups": { "backend": {} }, "order": [ { "group": "frontnd", "needs": ["backend"] } ] }
        """);

        Assert.Contains(errors, e => e.Contains("frontnd") && e.Contains("backend"));
    }

    [Fact]
    public void AnUndefinedNeedIsAnError()
    {
        var errors = ReadErrors("""
        { "version": 1, "groups": { "a": {} }, "order": [ { "group": "a", "needs": ["ghost"] } ] }
        """);

        Assert.Contains(errors, e => e.Contains("ghost"));
    }

    [Fact]
    public void AGroupNeedingItselfIsAnError()
    {
        var errors = ReadErrors("""
        { "version": 1, "groups": { "a": {} }, "order": [ { "group": "a", "needs": ["a"] } ] }
        """);

        Assert.Contains(errors, e => e.Contains("needing itself"));
    }

    [Fact]
    public void ARuleThatNeedsNothingIsAnError()
    {
        // It would order nothing, so writing one is a mistake rather than a no-op worth keeping.
        var errors = ReadErrors("""
        { "version": 1, "groups": { "a": {} }, "order": [ { "group": "a", "needs": [] } ] }
        """);

        Assert.Contains(errors, e => e.Contains("at least one group"));
    }

    [Fact]
    public void AnUnknownEnumValueListsTheValidOnes()
    {
        var errors = ReadErrors("""
        { "version": 1, "groups": { "a": {}, "b": {} },
          "order": [ { "group": "a", "needs": ["b"], "scope": "per_task" } ] }
        """);

        Assert.Contains(errors, e => e.Contains("across_plan") && e.Contains("within_task"));
    }

    [Fact]
    public void AnOverrideWithoutAMatchIsRefused()
    {
        // Without a selector it would apply to every task, which is what the top-level policy is for.
        var errors = ReadErrors("""
        { "version": 1, "tasks": { "overrides": [ { "wait_for_subtasks": false } ] } }
        """);

        Assert.Contains(errors, e => e.Contains("match") && e.Contains("every task"));
    }

    [Fact]
    public void AFutureVersionIsRefusedRatherThanReadWithTodaysRules()
    {
        Assert.Contains(ReadErrors("""{ "version": 2 }"""), e => e.Contains("Unsupported version 2"));
    }

    [Fact]
    public void MalformedJsonSaysSo()
        => Assert.Contains(ReadErrors("{ nope"), e => e.Contains("not valid JSON"));

    [Fact]
    public void EveryProblemIsReportedNotJustTheFirst()
    {
        // A rule file is edited in bulk; one mistake per round trip turns a short edit into an afternoon.
        var errors = ReadErrors("""
        { "version": 1,
          "groups": { "a": { "bogus": [] } },
          "order": [ { "group": "missing", "needs": ["alsoMissing"] } ] }
        """);

        Assert.True(errors.Count >= 3, $"expected several errors, got: {string.Join(" | ", errors)}");
    }

    [Fact]
    public void ReadsANestedExcludeSelector()
    {
        var rules = ReadValid("""
        { "version": 1,
          "groups": { "backend": { "repositories": ["group/svc-*"],
                                   "exclude": { "repositories": ["group/svc-db"] } } } }
        """);

        var backend = rules.Groups["backend"];
        Assert.NotNull(backend.Exclude);
        Assert.Equal(["group/svc-db"], backend.Exclude!.Repositories);
    }

    [Fact]
    public void CommentsAreAllowedSoARuleFileCanExplainItself()
    {
        // JSON has no comments, but this reader skips them -- and YAML has them natively, so allowing
        // them here keeps a document portable to the YAML reader unchanged.
        var rules = ReadValid("""
        {
          // db must land before the API that reads it
          "version": 1,
          "groups": { "a": {}, "b": {} },
          "order": [ { "group": "a", "needs": ["b"] } ]
        }
        """);

        Assert.Single(rules.Order);
    }
}
