using ReleaseOrchestrator.Providers.GitLab;
using Xunit;

namespace ReleaseOrchestrator.UnitTests.Providers.GitLab;

/// <summary>
/// Version detection is what answers "is this install new enough", and a self-hosted GitLab can
/// be any version - so the comparison has to be numeric. Text ordering puts 16.11 before 16.9,
/// which would disable a capability on a server that has it.
/// </summary>
public class GitLabServerVersionTests
{
    [Theory]
    [InlineData("17.5.1", 17, 5, 1)]
    [InlineData("16.11.0", 16, 11, 0)]
    // Edition and pre-release suffixes: only the numeric head orders releases.
    [InlineData("16.11.0-ee", 16, 11, 0)]
    [InlineData("15.0.0-pre", 15, 0, 0)]
    [InlineData("14.3.2+ce.0", 14, 3, 2)]
    // A version reported with fewer components than three.
    [InlineData("17.5", 17, 5, 0)]
    [InlineData("17", 17, 0, 0)]
    [InlineData("  17.5.1  ", 17, 5, 1)]
    public void ParsesTheNumericHead(string raw, int major, int minor, int patch)
    {
        Assert.True(GitLabServerVersion.TryParse(raw, out var version));
        Assert.Equal(major, version.Major);
        Assert.Equal(minor, version.Minor);
        Assert.Equal(patch, version.Patch);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("unknown")]
    [InlineData("v17.5.1")]
    public void RefusesWhatItCannotOrder(string? raw)
        => Assert.False(GitLabServerVersion.TryParse(raw, out _));

    [Fact]
    public void OrdersMinorVersionsNumericallyNotAsText()
    {
        // The whole reason this type exists: "16.9" > "16.11" as strings.
        Assert.True(Parse("16.9.0") < Parse("16.11.0"));
        Assert.True(Parse("16.11.0") > Parse("16.9.0"));
    }

    [Fact]
    public void OrdersByMajorThenMinorThenPatch()
    {
        Assert.True(Parse("15.0.0") < Parse("16.0.0"));
        Assert.True(Parse("17.5.1") > Parse("17.5.0"));
        Assert.True(Parse("17.5.0") <= Parse("17.5.0"));
        Assert.True(Parse("17.5.0") >= Parse("17.5.0"));
    }

    [Fact]
    public void EditionSuffixDoesNotAffectOrdering()
        => Assert.Equal(0, Parse("16.11.0-ee").CompareTo(Parse("16.11.0")));

    [Theory]
    [InlineData("13.9.0", 13, 9, true)]
    [InlineData("13.10.0", 13, 9, true)]
    [InlineData("14.0.0", 13, 9, true)]
    // Renovate gates reviewers on exactly this boundary; 13.8 is the case that must fail.
    [InlineData("13.8.9", 13, 9, false)]
    [InlineData("12.99.99", 13, 9, false)]
    public void IsAtLeastGatesOnTheBoundary(string raw, int major, int minor, bool expected)
        => Assert.Equal(expected, Parse(raw).IsAtLeast(major, minor));

    [Fact]
    public void KeepsTheRawStringForDiagnostics()
        => Assert.Equal("16.11.0-ee", Parse("16.11.0-ee").Raw);

    private static GitLabServerVersion Parse(string raw)
    {
        Assert.True(GitLabServerVersion.TryParse(raw, out var version));
        return version;
    }
}
