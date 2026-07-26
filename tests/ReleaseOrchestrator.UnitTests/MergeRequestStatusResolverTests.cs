using ReleaseOrchestrator.Core.Enums;
using ReleaseOrchestrator.Core.Parsing;
using Xunit;

namespace ReleaseOrchestrator.UnitTests;

/// <summary>
/// The rules over the normalized merge-request status. Both the webhook consumers and the VCS sync
/// route through here — they used to keep separate mappings, so the same MR got a different status
/// depending on which path imported it.
/// </summary>
/// <remarks>
/// Raw-state mapping used to be tested here (it moved to the GitLab adapter), and so did a coarse
/// "ready-for-deploy label" promotion — which is gone: deploy readiness is a per-environment rule over
/// signals now, not a status a label promotes an MR to. What remains is: terminal detection, and that
/// an open MR is Opened unless an operator pinned its status.
/// </remarks>
public class MergeRequestStatusResolverTests
{
    [Theory]
    [InlineData(MergeRequestStatus.Merged, true)]
    [InlineData(MergeRequestStatus.Closed, true)]
    [InlineData(MergeRequestStatus.Opened, false)]
    [InlineData(MergeRequestStatus.ReadyForDeploy, false)]
    public void TerminalStatesAreTheOnesTheVcsDecides(MergeRequestStatus status, bool expected)
        => Assert.Equal(expected, MergeRequestStatusResolver.IsTerminal(status));

    [Fact]
    public void AnOpenMergeRequestIsOpened()
        => Assert.Equal(
            MergeRequestStatus.Opened,
            MergeRequestStatusResolver.ResolveOpenStatus(isStatusManual: false, MergeRequestStatus.Opened));

    /// <summary>An operator's manually pinned status survives a later observation.</summary>
    [Theory]
    [InlineData(MergeRequestStatus.ReadyForDeploy)]
    [InlineData(MergeRequestStatus.Opened)]
    public void ManualStatusIsPreserved(MergeRequestStatus pinned)
        => Assert.Equal(
            pinned,
            MergeRequestStatusResolver.ResolveOpenStatus(isStatusManual: true, pinned));
}
