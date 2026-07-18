using ReleaseOrchestrator.Infrastructure.Ingestion;
using Xunit;

namespace ReleaseOrchestrator.UnitTests.Ingestion;

/// <summary>
/// Covers the polling event id: stable for an unchanged merge request (so a re-poll self-dedups) and
/// different when something that matters changes (so a real change is processed).
/// </summary>
public class PollingEventIdTests
{
    private static string Id(string status = "Opened", params string[] labels) =>
        PollingEventId.For("gitlab/default", "group/svc", "123", status, labels);

    [Fact]
    public void SameInputs_ProduceSameId()
    {
        Assert.Equal(Id("Opened", "a", "b"), Id("Opened", "a", "b"));
        // Label order must not matter -- the same set is the same observation.
        Assert.Equal(Id("Opened", "a", "b"), Id("Opened", "b", "a"));
    }

    [Fact]
    public void StatusChange_ProducesDifferentId()
    {
        Assert.NotEqual(Id("Opened"), Id("ReadyForDeploy"));
    }

    [Fact]
    public void LabelChange_ProducesDifferentId()
    {
        Assert.NotEqual(Id("Opened", "a"), Id("Opened", "a", "b"));
    }

    [Fact]
    public void DifferentMergeRequest_ProducesDifferentId()
    {
        Assert.NotEqual(
            PollingEventId.For("gitlab/default", "group/svc", "123", "Opened", []),
            PollingEventId.For("gitlab/default", "group/svc", "124", "Opened", []));
    }
}
