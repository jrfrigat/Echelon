using ReleaseOrchestrator.Providers.GitLab;
using Xunit;

namespace ReleaseOrchestrator.UnitTests.Providers.GitLab;

/// <summary>
/// Moved here with the parser itself, cases unchanged: the branch-key format is a dialect, so it
/// left Core for the adapter and its coverage came along rather than being rewritten. Every case
/// below is a bug that happened once.
/// </summary>
public class GitLabBranchTaskParserTests
{
    [Theory]
    [InlineData("feature/PROJ-123-add-thing", "PROJ-123")]
    [InlineData("PROJ-123", "PROJ-123")]
    [InlineData("bugfix/ABC-1", "ABC-1")]
    // Digits inside the key: the ingress copy required [A-Z]+ and missed these.
    [InlineData("feature/S3-42-migrate", "S3-42")]
    [InlineData("feature/A1B-2-thing", "A1B-2")]
    // Single-letter key: the infrastructure copy required [A-Z][A-Z0-9]+ and missed these.
    [InlineData("feature/X-1", "X-1")]
    public void ExtractsIssueKey(string branch, string expected)
        => Assert.Equal(expected, GitLabBranchTaskParser.ParseTaskId(branch));

    [Theory]
    [InlineData("main")]
    [InlineData("feature/no-key-here")]
    [InlineData("feature/lower-12")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void ReturnsNullWhenNoKeyIsPresent(string? branch)
        => Assert.Null(GitLabBranchTaskParser.ParseTaskId(branch));

    [Fact]
    public void DoesNotMatchMidTokenInsideADateLikeSegment()
    {
        // release/2024-01-ABC-15 must not yield "01-ABC" or a key glued to the date.
        Assert.Equal("ABC-15", GitLabBranchTaskParser.ParseTaskId("release/2024-01-ABC-15"));
    }

    [Fact]
    public void DoesNotMatchAKeyGluedToTrailingDigits()
        => Assert.Null(GitLabBranchTaskParser.ParseTaskId("feature/ABC-15x9"));

    [Fact]
    public void NullInputDoesNotThrow()
    {
        // SourceBranch is non-nullable in the entity, but arrives from JSON deserialization
        // where a missing field yields null regardless.
        var exception = Record.Exception(() => GitLabBranchTaskParser.ParseTaskId(null));
        Assert.Null(exception);
    }
}
