using Echelon.Core.Enums;
using Echelon.Providers.GitLab;
using Xunit;

namespace Echelon.UnitTests.Providers.GitLab;

/// <summary>
/// Moved here with the state dictionary, cases unchanged. These strings are GitLab's vocabulary,
/// so the mapping left Core with them; what stayed behind - <c>IsTerminal</c> and label-driven
/// promotion - is still covered by <c>MergeRequestStatusResolverTests</c>.
/// </summary>
public class GitLabMergeRequestStateTests
{
    [Theory]
    [InlineData("opened", MergeRequestStatus.Opened)]
    [InlineData("reopened", MergeRequestStatus.Opened)]
    [InlineData("merged", MergeRequestStatus.Merged)]
    [InlineData("closed", MergeRequestStatus.Closed)]
    [InlineData("OPENED", MergeRequestStatus.Opened)]
    public void MapsKnownVcsStates(string state, MergeRequestStatus expected)
        => Assert.Equal(expected, GitLabMergeRequestState.FromState(state));

    [Theory]
    [InlineData("locked")]
    [InlineData("")]
    [InlineData(null)]
    public void DoesNotGuessAtUnknownStates(string? state)
        => Assert.Null(GitLabMergeRequestState.FromState(state));
}
