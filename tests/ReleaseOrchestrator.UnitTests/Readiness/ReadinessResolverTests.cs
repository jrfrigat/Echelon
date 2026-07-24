using ReleaseOrchestrator.Core.Enums;
using ReleaseOrchestrator.Core.Parsing;
using Xunit;

namespace ReleaseOrchestrator.UnitTests.Readiness;

/// <summary>
/// Covers the predicate that stands between unreviewed code and production.
/// </summary>
/// <remarks>
/// The empty-rule cases are the point of this file. Everything else here is arithmetic; those two
/// are the difference between "an unconfigured gate admits nothing" and "an unconfigured gate admits
/// everything", and the second is reachable through nothing worse than <c>All()</c> over an empty
/// sequence being vacuously true.
///
/// This file also exists because the previous gate test was NAMED for a guarantee it did not check
/// (<c>MissingLabelKeepsTheMrOutOfThePlan</c> asserted only that a resolver returned a status), and
/// the test that did check it was deleted with the global plan and never replaced.
/// </remarks>
public class ReadinessResolverTests
{
    private static string[] Labels(params string[] labels) => LabelSet.Normalize(labels).ToArray();

    // ---- the rules ------------------------------------------------------------

    [Fact]
    public void NoGateAdmitsAnything() =>
        Assert.True(ReadinessResolver.IsReady(Labels(), Labels(), ReadyRule.NoGate));

    [Fact]
    public void AnyOfAdmitsOnOneMatchingLabel() =>
        Assert.True(ReadinessResolver.IsReady(
            Labels("bug", "ready-for-prod"), Labels("ready-for-prod", "hotfix"), ReadyRule.AnyOf));

    [Fact]
    public void AnyOfRefusesWhenNoLabelMatches() =>
        Assert.False(ReadinessResolver.IsReady(
            Labels("bug", "ready-for-test"), Labels("ready-for-prod"), ReadyRule.AnyOf));

    [Fact]
    public void AllOfRequiresEveryConfiguredLabel()
    {
        Assert.True(ReadinessResolver.IsReady(
            Labels("approved", "qa-signed", "extra"), Labels("approved", "qa-signed"), ReadyRule.AllOf));

        Assert.False(ReadinessResolver.IsReady(
            Labels("approved"), Labels("approved", "qa-signed"), ReadyRule.AllOf));
    }

    // ---- the cases this file exists for ---------------------------------------

    /// <summary>
    /// A gate that is switched on with nothing configured admits NOTHING.
    /// </summary>
    /// <remarks>
    /// The natural implementation of AllOf — <c>ruleLabels.All(...)</c> — returns true for an empty
    /// rule set, so an environment whose gate was enabled but never configured would admit every
    /// merge request in the system while appearing, in the admin UI, to be gated. That is the exact
    /// shape of an accident nobody would detect until after a deploy.
    /// </remarks>
    [Theory]
    [InlineData(ReadyRule.AnyOf)]
    [InlineData(ReadyRule.AllOf)]
    public void AGateWithNoConfiguredLabelsAdmitsNothing(ReadyRule rule)
    {
        Assert.False(ReadinessResolver.IsReady(Labels("anything", "at", "all"), Labels(), rule));
        Assert.False(ReadinessResolver.IsReady(Labels(), Labels(), rule));
    }

    /// <summary>An unknown rule throws rather than guessing, because the convenient guess is "allowed".</summary>
    [Fact]
    public void AnUnknownRuleRefusesToGuess() =>
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            ReadinessResolver.IsReady(Labels("x"), Labels("x"), (ReadyRule)99));

    /// <summary>
    /// Every enum member is handled. A member added later without a branch here fails this test
    /// rather than falling into the throw at runtime, on a deploy.
    /// </summary>
    [Fact]
    public void EveryDeclaredRuleIsHandled()
    {
        foreach (var rule in Enum.GetValues<ReadyRule>())
        {
            var exception = Record.Exception(() =>
                ReadinessResolver.IsReady(Labels("ready"), Labels("ready"), rule));

            Assert.Null(exception);
        }
    }

    /// <summary>No enum in this feature may have a zero member: a never-written column must not mean "allowed".</summary>
    [Fact]
    public void NoReadinessEnumHasAZeroMember()
    {
        Assert.DoesNotContain(0, Enum.GetValues<ReadyRule>().Cast<int>());
        Assert.DoesNotContain(0, Enum.GetValues<RedeployPolicy>().Cast<int>());
        Assert.DoesNotContain(0, Enum.GetValues<ReadinessSource>().Cast<int>());
    }
}

/// <summary>
/// Covers the combination of a person's pin with the label rule — the escape hatch that keeps a
/// merged merge request from wedging a production gate, and the hold that keeps one out.
/// </summary>
public class ReadinessEvaluatorTests
{
    private static string[] Labels(params string[] labels) => LabelSet.Normalize(labels).ToArray();

    /// <summary>A pin to ready admits the merge request, and the labels are never consulted.</summary>
    [Fact]
    public void APinToReadyAdmitsRegardlessOfLabels()
    {
        // Labels would fail the AllOf rule, but the pin overrides them.
        var decision = ReadinessEvaluator.Evaluate(
            Labels("nothing-matching"), Labels("approved", "qa"), ReadyRule.AllOf, pinnedReady: true);

        Assert.True(decision.IsReady);
        Assert.Equal(ReadinessSource.Pin, decision.Source);
    }

    /// <summary>A pin to not-ready holds the merge request out, even when its labels would admit it.</summary>
    [Fact]
    public void APinToNotReadyHoldsRegardlessOfLabels()
    {
        // Labels satisfy AnyOf, but the pin holds it.
        var decision = ReadinessEvaluator.Evaluate(
            Labels("ready-for-prod"), Labels("ready-for-prod"), ReadyRule.AnyOf, pinnedReady: false);

        Assert.False(decision.IsReady);
        Assert.Equal(ReadinessSource.Pin, decision.Source);
    }

    /// <summary>With no pin, the labels decide against the rule, and the source is the labels.</summary>
    [Fact]
    public void WithNoPinTheLabelsDecide()
    {
        var admitted = ReadinessEvaluator.Evaluate(
            Labels("ready-for-prod"), Labels("ready-for-prod"), ReadyRule.AnyOf, pinnedReady: null);
        Assert.True(admitted.IsReady);
        Assert.Equal(ReadinessSource.Labels, admitted.Source);

        var refused = ReadinessEvaluator.Evaluate(
            Labels("ready-for-test"), Labels("ready-for-prod"), ReadyRule.AnyOf, pinnedReady: null);
        Assert.False(refused.IsReady);
        Assert.Equal(ReadinessSource.Labels, refused.Source);
    }

    /// <summary>An ungated environment admits through the labels path, not as a pin.</summary>
    [Fact]
    public void NoGateAdmitsThroughTheLabelsPath()
    {
        var decision = ReadinessEvaluator.Evaluate(
            Labels(), Labels(), ReadyRule.NoGate, pinnedReady: null);

        Assert.True(decision.IsReady);
        Assert.Equal(ReadinessSource.Labels, decision.Source);
    }
}

/// <summary>Covers label normalization, which decides whether two spellings are the same permission.</summary>
public class LabelSetTests
{
    [Fact]
    public void NormalizeTrimsLowercasesAndDeduplicates() =>
        Assert.Equal(
            ["ready-for-prod"],
            LabelSet.Normalize([" Ready-For-Prod ", "ready-for-prod", "READY-FOR-PROD"]));

    [Fact]
    public void NormalizeDropsBlanksAndNulls() =>
        Assert.Equal(["a"], LabelSet.Normalize(["a", "", "   ", null]));

    [Fact]
    public void NormalizeIsNullSafe() => Assert.Empty(LabelSet.Normalize(null));

    /// <summary>
    /// A label containing the join delimiter is dropped, not stored: it would otherwise be torn into
    /// separate tokens when the canonical set is split at the readiness gate and could match a rule
    /// label it never carried — a false admit into a gated environment.
    /// </summary>
    [Fact]
    public void NormalizeDropsALabelContainingTheDelimiter()
    {
        Assert.Equal(["ok"], LabelSet.Normalize(["ok", "foo,ready-for-prod"]));
        // The whole canonical set stays split-safe: nothing round-trips through the comma but real labels.
        Assert.Equal("ok", LabelSet.Canonical(["ok", "sneaky,ready-for-prod"]));
    }

    /// <summary>
    /// Order must not matter: the canonical form is a change-detection key, and a provider that
    /// happens to list labels differently on two deliveries must not read as a change.
    /// </summary>
    [Fact]
    public void CanonicalIsOrderIndependent() =>
        Assert.Equal(
            LabelSet.Canonical(["b", "a", "c"]),
            LabelSet.Canonical(["c", "b", "a"]));

    /// <summary>
    /// A set returning to a previous value is a real change, and the canonical form must reflect
    /// that — approve, revoke, approve again has to be observable.
    /// </summary>
    [Fact]
    public void CanonicalDistinguishesAddingAndRemovingALabel()
    {
        var approved = LabelSet.Canonical(["ready-for-prod"]);
        var revoked = LabelSet.Canonical([]);

        Assert.NotEqual(approved, revoked);
        Assert.Equal(approved, LabelSet.Canonical(["ready-for-prod"]));
    }
}
