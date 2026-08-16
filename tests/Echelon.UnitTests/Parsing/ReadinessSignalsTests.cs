using Echelon.Core.Enums;
using Echelon.Core.Parsing;
using Xunit;

namespace Echelon.UnitTests.Parsing;

/// <summary>
/// The signal set a merge request carries, which a readiness rule is matched against. One token per
/// label, one for the status, one for a pipeline result when known - canonical so the comparison the
/// gate does is a byte match.
/// </summary>
public class ReadinessSignalsTests
{
    [Fact]
    public void BuildsLabelAndStatusSignals()
    {
        var signals = ReadinessSignals.For(["Ready-For-Prod", "bug"], MergeRequestStatus.Merged);

        // Lower-cased and prefixed by kind; status is always present.
        Assert.Contains("label:ready-for-prod", signals);
        Assert.Contains("label:bug", signals);
        Assert.Contains("mr-status:merged", signals);
    }

    [Fact]
    public void IncludesAPipelineSignalWhenKnownAndOmitsItWhenNot()
    {
        Assert.Contains("pipeline:success",
            ReadinessSignals.For([], MergeRequestStatus.Opened, pipelineResult: "success"));

        Assert.DoesNotContain(
            ReadinessSignals.For([], MergeRequestStatus.Opened),
            s => s.StartsWith("pipeline:", System.StringComparison.Ordinal));
    }

    [Fact]
    public void IsCanonical_SortedDedupedAndCommaFree()
    {
        var signals = ReadinessSignals.For(["b", "a", "a", "has,comma"], MergeRequestStatus.Opened);

        Assert.Equal(signals.OrderBy(s => s, System.StringComparer.Ordinal), signals);          // sorted
        Assert.Equal(signals.Distinct().Count(), signals.Count);                                 // de-duped
        Assert.DoesNotContain(signals, s => s.Contains(',', System.StringComparison.Ordinal));   // no delimiter
    }
}
