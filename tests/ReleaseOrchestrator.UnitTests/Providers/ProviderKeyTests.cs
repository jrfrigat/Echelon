using ReleaseOrchestrator.Providers.Abstractions;
using Xunit;

namespace ReleaseOrchestrator.UnitTests.Providers;

/// <summary>
/// The provider key replaced an enum, which means the compiler no longer rejects a bad value —
/// so normalization is what stops "GitLab", "gitlab" and " gitlab " from being three providers.
/// The PWA still sends "GitLab" and the database now stores "gitlab".
/// </summary>
public class ProviderKeyTests
{
    [Theory]
    [InlineData("gitlab", "gitlab")]
    [InlineData("GitLab", "gitlab")]
    [InlineData("GITLAB", "gitlab")]
    [InlineData("  GitLab  ", "gitlab")]
    [InlineData("YandexTracker", "yandextracker")]
    public void NormalizesCaseAndWhitespace(string input, string expected)
        => Assert.Equal(expected, ProviderKey.Normalize(input));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankNormalizesToEmptyRatherThanNull(string? input)
    {
        // Empty rather than null so a lookup misses and reports the available providers, instead
        // of throwing a NullReferenceException somewhere less useful.
        Assert.Equal(string.Empty, ProviderKey.Normalize(input));
    }

    [Theory]
    [InlineData("GitLab", "gitlab", true)]
    [InlineData("  gitlab ", "GITLAB", true)]
    [InlineData("gitlab", "github", false)]
    [InlineData(null, "", true)]
    public void MatchesComparesCanonicalForms(string? left, string? right, bool expected)
        => Assert.Equal(expected, ProviderKey.Matches(left, right));
}
