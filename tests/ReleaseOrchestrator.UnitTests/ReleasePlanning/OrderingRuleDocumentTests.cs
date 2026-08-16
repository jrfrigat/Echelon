using ReleaseOrchestrator.Application.ReleasePlanning;
using ReleaseOrchestrator.Core.Enums;
using Xunit;

namespace ReleaseOrchestrator.UnitTests.ReleasePlanning;

/// <summary>
/// Reading an ordering-rule document, and refusing to read a wrong one quietly.
/// </summary>
/// <remarks>
/// The document decides deploy order, so the expensive failure is not a rejected file - it is an
/// accepted one that means something other than what was written. Most of these are about that:
/// unknown keys, undefined groups and ambiguous values are errors, never skipped.
/// </remarks>
public class OrderingRuleDocumentTests
{
    static OrderingRules ReadValid(string yaml)
    {
        var result = OrderingRuleDocument.Read(yaml);
        Assert.True(result.IsValid, $"expected a valid document, got: {string.Join(" | ", result.Errors)}");
        return result.Rules!;
    }

    static IReadOnlyList<string> ReadErrors(string yaml)
    {
        var result = OrderingRuleDocument.Read(yaml);
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
            version: 1

            tasks:
              wait_for_subtasks: true
              wait_for_linked: true
              group_order: together

            groups:
              backend-main:
                connectors: ["gitlab-main"]
                repositories: ["group/svc-db", "group/svc-api"]
              frontend-partner:
                connectors: ["gitlab-partner"]
                repositories: ["partner/*"]

            order:
              - group: frontend-partner
                needs: [backend-main]
                type: hard
                scope: within_task
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
    public void ADocumentSavedAsJsonStillLoads()
    {
        // JSON is valid YAML, which is what lets a document stored before the YAML reader existed keep
        // working without anyone rewriting it.
        var rules = ReadValid("""
            { "version": 1,
              "groups": { "a": {}, "b": {} },
              "order": [ { "group": "a", "needs": ["b"] } ] }
            """);

        Assert.Single(rules.Order);
    }

    [Fact]
    public void CommentsAreAllowedSoARuleFileCanExplainItself()
    {
        var rules = ReadValid("""
            version: 1
            groups:
              db: {}          # the migrations repository
              api: {}
            order:
              # the API reads tables the migration creates
              - group: api
                needs: [db]
            """);

        Assert.Single(rules.Order);
    }

    [Fact]
    public void OmittedPolicyFieldsStayNullSoTheStoredDefaultIsLeftAlone()
    {
        var rules = ReadValid("""
            version: 1
            tasks:
              wait_for_subtasks: false
            """);

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
            version: 1
            groups: { a: {}, b: {} }
            order:
              - group: a
                needs: [b]
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
            version: 1
            groups:
              a:
                repositores: ["x"]
            """);

        Assert.Contains(errors, e => e.Contains("repositores") && e.Contains("Valid keys"));
    }

    [Fact]
    public void ErrorsCarryTheLineToEdit()
    {
        var errors = ReadErrors("""
            version: 1
            groups:
              a:
                bogus: ["x"]
            """);

        Assert.Contains(errors, e => e.Contains("Line 4"));
    }

    [Fact]
    public void AnUndefinedGroupIsNamedAlongWithWhatWasAvailable()
    {
        var errors = ReadErrors("""
            version: 1
            groups: { backend: {} }
            order:
              - group: frontnd
                needs: [backend]
            """);

        Assert.Contains(errors, e => e.Contains("frontnd") && e.Contains("backend"));
    }

    [Fact]
    public void AGroupNeedingItselfIsAnError()
    {
        var errors = ReadErrors("""
            version: 1
            groups: { a: {} }
            order:
              - group: a
                needs: [a]
            """);

        Assert.Contains(errors, e => e.Contains("needing itself"));
    }

    [Fact]
    public void ARuleThatNeedsNothingIsAnError()
    {
        // It would order nothing, so writing one is a mistake rather than a no-op worth keeping.
        var errors = ReadErrors("""
            version: 1
            groups: { a: {} }
            order:
              - group: a
                needs: []
            """);

        Assert.Contains(errors, e => e.Contains("at least one group"));
    }

    [Fact]
    public void AnUnknownEnumValueListsTheValidOnes()
    {
        var errors = ReadErrors("""
            version: 1
            groups: { a: {}, b: {} }
            order:
              - group: a
                needs: [b]
                scope: per_task
            """);

        Assert.Contains(errors, e => e.Contains("across_plan") && e.Contains("within_task"));
    }

    [Theory]
    [InlineData("no")]
    [InlineData("yes")]
    [InlineData("on")]
    [InlineData("True")]
    public void AnAmbiguousBooleanIsRefusedRatherThanGuessed(string value)
    {
        // The Norway problem: YAML's implicit typing reads some of these as booleans and the exact set
        // depends on the version. On a switch that decides whether a rollout waits for its subtasks, an
        // ambiguous spelling is better refused than resolved by a rule nobody remembers.
        var errors = ReadErrors($"""
            version: 1
            tasks:
              wait_for_subtasks: {value}
            """);

        Assert.Contains(errors, e => e.Contains("must be true or false"));
    }

    [Fact]
    public void AnOverrideWithoutAMatchIsRefused()
    {
        // Without a selector it would apply to every task, which is what the top-level policy is for.
        var errors = ReadErrors("""
            version: 1
            tasks:
              overrides:
                - wait_for_subtasks: false
            """);

        Assert.Contains(errors, e => e.Contains("match") && e.Contains("every task"));
    }

    [Fact]
    public void ReadsAPerTaskOverride()
    {
        var rules = ReadValid("""
            version: 1
            tasks:
              overrides:
                - match: { task_keys: ["INFRA-*"] }
                  wait_for_subtasks: false
            """);

        var over = Assert.Single(rules.Tasks.Overrides);
        Assert.Equal(["INFRA-*"], over.Match.TaskKeys);
        Assert.False(over.WaitForSubtasks);
    }

    [Fact]
    public void ReadsANestedExcludeSelector()
    {
        var rules = ReadValid("""
            version: 1
            groups:
              backend:
                repositories: ["group/svc-*"]
                exclude:
                  repositories: ["group/svc-db"]
            """);

        var backend = rules.Groups["backend"];
        Assert.NotNull(backend.Exclude);
        Assert.Equal(["group/svc-db"], backend.Exclude!.Repositories);
    }

    [Fact]
    public void TheShapeGeneratedFromTheOnScreenRulesParses()
    {
        // What "fill from the rules below" writes into the editor. It exists so adopting the document
        // is a copy-paste rather than a re-typing, which only holds if what it writes is accepted --
        // a generator emitting something the reader rejects would be worse than no button.
        var rules = ReadValid("""
            version: 1

            # Generated from the repository-ordering rules that were configured on screen.
            # Review it, then save to make the document the source of truth.

            groups:
              group-svc-backend:
                repositories: ["group/svc-backend"]
              group-svc-db:
                repositories: ["group/svc-db"]
              group-web:
                repositories: ["group/web"]

            order:
              - group: group-svc-backend
                needs: [group-svc-db]
                type: hard
              - group: group-web
                needs: [group-svc-backend]
                type: soft
            """);

        Assert.Equal(3, rules.Groups.Count);
        Assert.Equal(2, rules.Order.Count);
        Assert.Equal(StackDependencyType.Soft, rules.Order[1].Type);
    }

    [Fact]
    public void AFutureVersionIsRefusedRatherThanReadWithTodaysRules()
        => Assert.Contains(ReadErrors("version: 2"), e => e.Contains("Unsupported version 2"));

    [Fact]
    public void MalformedYamlSaysWhere()
        => Assert.Contains(ReadErrors("version: 1\ngroups:\n  a: [unclosed"), e => e.Contains("not valid YAML"));

    [Fact]
    public void ASecondDocumentIsRefusedRatherThanIgnored()
    {
        // A stray `---` would otherwise silently drop everything after it.
        var errors = ReadErrors("""
            version: 1
            ---
            version: 1
            """);

        Assert.Contains(errors, e => e.Contains("more than one YAML document"));
    }

    [Fact]
    public void EveryProblemIsReportedNotJustTheFirst()
    {
        // A rule file is edited in bulk; one mistake per round trip turns a short edit into an afternoon.
        var errors = ReadErrors("""
            version: 1
            groups:
              a:
                bogus: []
            order:
              - group: missing
                needs: [alsoMissing]
            """);

        Assert.True(errors.Count >= 3, $"expected several errors, got: {string.Join(" | ", errors)}");
    }
}
