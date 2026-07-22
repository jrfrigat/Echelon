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
