using ReleaseOrchestrator.Core.Enums;
using ReleaseOrchestrator.Core.Parsing;
using Xunit;

namespace ReleaseOrchestrator.UnitTests;

/// <summary>
/// The rules that decide whether a merge request enters the release plan. Both the webhook
/// consumers and the VCS sync route through here — they used to keep separate mappings, so the
/// same MR got a different status depending on which path imported it.
/// </summary>
public class MergeRequestStatusResolverTests
{
    [Theory]
    [InlineData("opened", MergeRequestStatus.Opened)]
    [InlineData("reopened", MergeRequestStatus.Opened)]
    [InlineData("merged", MergeRequestStatus.Merged)]
    [InlineData("closed", MergeRequestStatus.Closed)]
    [InlineData("OPENED", MergeRequestStatus.Opened)]
    public void MapsKnownVcsStates(string state, MergeRequestStatus expected)
        => Assert.Equal(expected, MergeRequestStatusResolver.FromVcsState(state));

    [Theory]
    [InlineData("locked")]
    [InlineData("")]
    [InlineData(null)]
    public void DoesNotGuessAtUnknownStates(string? state)
        => Assert.Null(MergeRequestStatusResolver.FromVcsState(state));

    [Theory]
    [InlineData(MergeRequestStatus.Merged, true)]
    [InlineData(MergeRequestStatus.Closed, true)]
    [InlineData(MergeRequestStatus.Opened, false)]
    [InlineData(MergeRequestStatus.ReadyForDeploy, false)]
    public void TerminalStatesAreTheOnesTheVcsDecides(MergeRequestStatus status, bool expected)
        => Assert.Equal(expected, MergeRequestStatusResolver.IsTerminal(status));

    // ---- label-driven promotion (README §5) -----------------------------------

    [Fact]
    public void LabelPromotesToReadyForDeploy()
        => Assert.Equal(
            MergeRequestStatus.ReadyForDeploy,
            MergeRequestStatusResolver.ResolveOpenStatus(
                ["ready-for-deploy"], "ready-for-deploy", isStatusManual: false, MergeRequestStatus.Opened));

    [Fact]
    public void MissingLabelKeepsTheMrOutOfThePlan()
        => Assert.Equal(
            MergeRequestStatus.Opened,
            MergeRequestStatusResolver.ResolveOpenStatus(
                ["bug", "backend"], "ready-for-deploy", isStatusManual: false, MergeRequestStatus.Opened));

    [Fact]
    public void RemovingTheLabelDemotesAnAlreadyPromotedMr()
        => Assert.Equal(
            MergeRequestStatus.Opened,
            MergeRequestStatusResolver.ResolveOpenStatus(
                [], "ready-for-deploy", isStatusManual: false, MergeRequestStatus.ReadyForDeploy));

    [Theory]
    [InlineData("Ready-For-Deploy")]
    [InlineData("  ready-for-deploy  ")]
    public void LabelMatchIsCaseInsensitiveAndTrimmed(string label)
        => Assert.Equal(
            MergeRequestStatus.ReadyForDeploy,
            MergeRequestStatusResolver.ResolveOpenStatus(
                [label], "ready-for-deploy", isStatusManual: false, MergeRequestStatus.Opened));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NoConfiguredLabelDisablesPromotion(string? configured)
    {
        // A connection without a marker label opts out of label-driven promotion entirely;
        // its MRs reach the plan only through the manual API.
        Assert.Equal(
            MergeRequestStatus.Opened,
            MergeRequestStatusResolver.ResolveOpenStatus(
                ["ready-for-deploy"], configured, isStatusManual: false, MergeRequestStatus.Opened));
    }

    [Fact]
    public void ManualPinSurvivesAWebhookWithoutTheLabel()
    {
        // An operator promoted this MR by hand; a later push must not silently undo that.
        Assert.Equal(
            MergeRequestStatus.ReadyForDeploy,
            MergeRequestStatusResolver.ResolveOpenStatus(
                [], "ready-for-deploy", isStatusManual: true, MergeRequestStatus.ReadyForDeploy));
    }

    [Fact]
    public void ManualPinAlsoSurvivesWhenTheLabelIsPresent()
        => Assert.Equal(
            MergeRequestStatus.Opened,
            MergeRequestStatusResolver.ResolveOpenStatus(
                ["ready-for-deploy"], "ready-for-deploy", isStatusManual: true, MergeRequestStatus.Opened));

    [Fact]
    public void NullLabelCollectionIsTreatedAsNoLabels()
        => Assert.Equal(
            MergeRequestStatus.Opened,
            MergeRequestStatusResolver.ResolveOpenStatus(
                null, "ready-for-deploy", isStatusManual: false, MergeRequestStatus.Opened));
}
