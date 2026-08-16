using Echelon.Providers.Abstractions;
using Xunit;

namespace Echelon.UnitTests.Providers;

/// <summary>
/// The message is the feature. A provider type is stored per connection, so this fires on a row
/// an operator typed, possibly long after deployment - and "provider 'gitab' is not registered"
/// is only actionable next to the list of what would have worked. Renovate's setPlatformApi
/// throws the same shape for the same reason.
/// </summary>
public class UnknownProviderExceptionTests
{
    [Fact]
    public void NamesTheMissingProviderAndListsTheRegisteredOnes()
    {
        var exception = new UnknownProviderException("VCS", "gitab", ["gitlab", "github"]);

        Assert.Contains("gitab", exception.Message, StringComparison.Ordinal);
        Assert.Contains("gitlab", exception.Message, StringComparison.Ordinal);
        Assert.Contains("github", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ListsProvidersInAStableOrder()
    {
        // DI registration order is not something a reader should have to reason about.
        var exception = new UnknownProviderException("VCS", "x", ["gitlab", "azure", "github"]);

        Assert.Contains("azure, github, gitlab", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SaysSoWhenNothingIsRegisteredAtAll()
    {
        // A different failure with a different fix: the composition root registered no adapter,
        // rather than the operator mistyping one.
        var exception = new UnknownProviderException("VCS", "gitlab", []);

        Assert.Contains("No VCS provider is registered", exception.Message, StringComparison.Ordinal);
    }
}
