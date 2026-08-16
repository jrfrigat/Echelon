using Echelon.Application.ReleasePlanning;
using Xunit;

namespace Echelon.UnitTests.ReleasePlanning;

/// <summary>
/// The shape rules of the plan document - what it accepts, what it refuses, and what it ignores.
/// </summary>
public class PlanDocumentReaderTests
{
    private const string Minimal = """
        version: 1
        target_task: PROJ-1
        nodes:
          - task: PROJ-1
            merge_requests:
              - mr: vcs:group/api!1
                wave: 1
        """;

    [Fact]
    public void ReadsAMinimalDocument()
    {
        var result = PlanDocumentReader.Read(Minimal);

        Assert.True(result.IsValid);
        Assert.Equal("PROJ-1", result.Document!.TargetTaskKey);
        var node = Assert.Single(result.Document.Nodes);
        var item = Assert.Single(node.Items);
        Assert.Equal("vcs:group/api!1", item.MergeRequestKey);
        Assert.Equal(1, item.Wave);
    }

    /// <summary>
    /// The exporter's read-only keys are accepted and ignored, so an exported plan can be edited and
    /// posted straight back. A schema that could not round-trip is a schema nobody uses.
    /// </summary>
    [Fact]
    public void OutputOnlyKeysAreIgnoredRatherThanRefused()
    {
        var result = PlanDocumentReader.Read("""
            version: 1
            target_task: PROJ-1
            plan_version: 7
            nodes:
              - task: PROJ-1
                depends_on: [PROJ-2]
                merge_requests:
                  - mr: vcs:group/api!1
                    wave: 1
            conflicts:
              - dropped: TaskDependency
                from: vcs:group/api!1
                to: vcs:group/web!2
                reason: whatever
            """);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void AMistypedKeyIsAnError()
    {
        var result = PlanDocumentReader.Read(Minimal.Replace("merge_requests:", "merge_request:"));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("unknown key 'merge_request'"));
    }

    [Fact]
    public void AMissingWaveIsAnError()
    {
        var result = PlanDocumentReader.Read("""
            version: 1
            target_task: PROJ-1
            nodes:
              - task: PROJ-1
                merge_requests:
                  - mr: vcs:group/api!1
            """);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("wave"));
    }

    [Fact]
    public void AZeroWaveIsAnError()
    {
        var result = PlanDocumentReader.Read(Minimal.Replace("wave: 1", "wave: 0"));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("1-based"));
    }

    /// <summary>The same merge request twice is two contradictory instructions, whichever node it sits in.</summary>
    [Fact]
    public void ARepeatedMergeRequestIsAnError()
    {
        var result = PlanDocumentReader.Read("""
            version: 1
            target_task: PROJ-1
            nodes:
              - task: PROJ-1
                merge_requests:
                  - mr: vcs:group/api!1
                    wave: 1
              - task: PROJ-2
                merge_requests:
                  - mr: vcs:group/api!1
                    wave: 2
            """);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("more than once"));
    }

    [Fact]
    public void ARepeatedTaskIsAnError()
    {
        var result = PlanDocumentReader.Read("""
            version: 1
            target_task: PROJ-1
            nodes:
              - task: PROJ-1
              - task: PROJ-1
            """);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("more than once"));
    }

    /// <summary>Refused rather than read with today's rules, as in the ordering-rule document.</summary>
    [Fact]
    public void AFutureVersionIsRefused()
    {
        var result = PlanDocumentReader.Read(Minimal.Replace("version: 1", "version: 2"));

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("Unsupported version 2"));
    }

    /// <summary>Unlike the ordering rules, an empty plan document is always a mistake.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AnEmptyDocumentIsAnError(string? text)
    {
        var result = PlanDocumentReader.Read(text);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, e => e.Contains("empty"));
    }

    [Fact]
    public void EveryProblemIsReportedInOnePass()
    {
        var result = PlanDocumentReader.Read("""
            version: 1
            target_task: PROJ-1
            nodes:
              - task: PROJ-1
                merge_requests:
                  - mr: vcs:group/api!1
                    wave: 0
              - task: PROJ-2
                merge_requests:
                  - mr: vcs:group/web!2
                    wave: -1
            """);

        Assert.False(result.IsValid);
        Assert.Equal(2, result.Errors.Count);
    }

    /// <summary>JSON is valid YAML, so a client that assembles the document need not emit YAML.</summary>
    [Fact]
    public void JsonIsAccepted()
    {
        var result = PlanDocumentReader.Read(
            """{"version":1,"target_task":"PROJ-1","nodes":[{"task":"PROJ-1","merge_requests":[{"mr":"vcs:group/api!1","wave":1}]}]}""");

        Assert.True(result.IsValid);
        Assert.Equal("PROJ-1", result.Document!.TargetTaskKey);
    }
}
