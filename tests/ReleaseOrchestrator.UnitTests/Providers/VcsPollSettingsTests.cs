using ReleaseOrchestrator.Providers.Abstractions.Vcs;
using Xunit;

namespace ReleaseOrchestrator.UnitTests.Providers;

/// <summary>
/// The poll interval moved from a column to a provider setting in the webhook/poll split, so the
/// poller reads it from the bag. A missing or unparsable value must fall back to the default rather
/// than to zero, which would busy-loop the poller.
/// </summary>
public class VcsPollSettingsTests
{
    [Fact]
    public void ReadsTheConfiguredInterval()
    {
        var settings = new Dictionary<string, string> { [VcsPollSettings.IntervalKey] = "120" };

        Assert.Equal(120, VcsPollSettings.IntervalFrom(settings));
    }

    [Fact]
    public void FallsBackToTheDefaultWhenAbsent()
    {
        Assert.Equal(
            VcsPollSettings.DefaultIntervalSeconds,
            VcsPollSettings.IntervalFrom(new Dictionary<string, string>()));
    }

    [Theory]
    [InlineData("soon")]
    [InlineData("")]
    public void FallsBackToTheDefaultWhenUnparsable(string value)
    {
        var settings = new Dictionary<string, string> { [VcsPollSettings.IntervalKey] = value };

        Assert.Equal(VcsPollSettings.DefaultIntervalSeconds, VcsPollSettings.IntervalFrom(settings));
    }
}
