using ReleaseOrchestrator.Core.Enums;
using ReleaseOrchestrator.Core.Parsing;
using ReleaseOrchestrator.Providers.Abstractions.Vcs;
using Xunit;

namespace ReleaseOrchestrator.UnitTests.Parsing;

/// <summary>
/// The one rule that links a merge request to its task, now configurable (source + pattern) rather
/// than a provider dialect. The branch cases below are the old branch parser's, unchanged: each was
/// a bug that happened once. The rest cover the source and pattern being chosen per connection.
/// </summary>
public class TaskKeyExtractorTests
{
    private const string Default = TaskLinkSettings.DefaultPattern;

    [Theory]
    [InlineData("feature/PROJ-123-add-thing", "PROJ-123")]
    [InlineData("PROJ-123", "PROJ-123")]
    [InlineData("bugfix/ABC-1", "ABC-1")]
    // Digits inside the key: the ingress copy required [A-Z]+ and missed these.
    [InlineData("feature/S3-42-migrate", "S3-42")]
    [InlineData("feature/A1B-2-thing", "A1B-2")]
    // Single-letter key: the infrastructure copy required [A-Z][A-Z0-9]+ and missed these.
    [InlineData("feature/X-1", "X-1")]
    public void ExtractsTheKeyFromABranchWithTheDefaultPattern(string branch, string expected) =>
        Assert.Equal(expected, TaskKeyExtractor.Extract(TaskKeySource.Branch, Default, branch, title: null, labels: null));

    /// <summary>
    /// A task key written in Cyrillic, in the branch shapes such repositories actually use.
    /// </summary>
    /// <remarks>
    /// Yandex Tracker projects are routinely keyed in Cyrillic, so this is the common case for a
    /// Russian-language installation rather than an edge one - and a Latin-only pattern does not fail
    /// on those keys, it links nothing at all: no task, so no plan, no rollout, and no message saying
    /// why. Roughly six in ten merge requests were affected on the first repository this met.
    ///
    /// The names below are invented; what is copied from reality is their SHAPE - an author segment,
    /// a two- or four-letter project code, zero-padded numbers, and the trailing suffixes people add
    /// when they branch twice for one task.
    /// </remarks>
    [Theory]
    [InlineData("feature/dv/ПР-5639", "ПР-5639")]
    [InlineData("feature/dv/МПРО-1015", "МПРО-1015")]
    [InlineData("feature/dv/ТЕХ-90", "ТЕХ-90")]
    [InlineData("feature/dv/ЗД-47", "ЗД-47")]
    [InlineData("origin/Feature/d.petrov/ПР-00046886", "ПР-00046886")]
    // Trailing suffixes are cut at the token boundary, exactly as they are for a Latin key.
    [InlineData("feature/dv/ПР-00041379_09_01", "ПР-00041379")]
    [InlineData("feature/dv/АТП-0047832-11", "АТП-0047832")]
    [InlineData("hotfix/dv/ПР-9477", "ПР-9477")]
    public void ExtractsACyrillicKey(string branch, string expected) =>
        Assert.Equal(expected, TaskKeyExtractor.Extract(TaskKeySource.Branch, Default, branch, null, null));

    [Theory]
    [InlineData("main")]
    [InlineData("feature/no-key-here")]
    [InlineData("feature/lower-12")]
    // Shapes that legitimately name no task: hand-written branches with no key in any alphabet.
    [InlineData("bsn_feature_hotFix")]
    [InlineData("hotfix/dv/Control_update")]
    [InlineData("feature/dv/Corr_Error_bonus_v1")]
    // Lowercase Cyrillic is not a key, for the same reason lowercase Latin is not.
    [InlineData("feature/dv/фикс-123")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void ReturnsNullWhenTheBranchNamesNoKey(string? branch) =>
        Assert.Null(TaskKeyExtractor.Extract(TaskKeySource.Branch, Default, branch, title: null, labels: null));

    [Fact]
    public void DoesNotMatchMidTokenInsideADateLikeSegment() =>
        // release/2024-01-ABC-15 must not yield "01-ABC" or a key glued to the date.
        Assert.Equal("ABC-15", TaskKeyExtractor.Extract(TaskKeySource.Branch, Default, "release/2024-01-ABC-15", null, null));

    [Fact]
    public void DoesNotMatchAKeyGluedToTrailingDigits() =>
        Assert.Null(TaskKeyExtractor.Extract(TaskKeySource.Branch, Default, "feature/ABC-15x9", null, null));

    [Fact]
    public void ReadsTheKeyFromTheTitleWhenThatIsTheSource() =>
        Assert.Equal("PROJ-9", TaskKeyExtractor.Extract(TaskKeySource.Title, Default, branch: "feature/x", title: "PROJ-9: fix the thing", labels: null));

    [Fact]
    public void ReadsTheKeyFromTheFirstMatchingLabelWhenThatIsTheSource() =>
        Assert.Equal("PROJ-4", TaskKeyExtractor.Extract(
            TaskKeySource.Label, Default, branch: "feature/x", title: null, labels: ["bug", "PROJ-4", "PROJ-5"]));

    [Fact]
    public void HonoursACustomPattern()
    {
        // A team whose keys are '#123' rather than 'PROJ-12'.
        var key = TaskKeyExtractor.Extract(TaskKeySource.Branch, @"#(\d+)", "feature/#77-thing", null, null);
        Assert.Equal("77", key);
    }

    [Fact]
    public void AnInvalidPatternLinksNothingRatherThanThrowing()
    {
        var exception = Record.Exception(() =>
            Assert.Null(TaskKeyExtractor.Extract(TaskKeySource.Branch, "([unclosed", "feature/PROJ-1", null, null)));
        Assert.Null(exception);
    }

    [Fact]
    public void NullBranchDoesNotThrow()
    {
        // SourceBranch is non-nullable in the entity, but arrives from JSON where a missing field is null.
        var exception = Record.Exception(() => TaskKeyExtractor.Extract(TaskKeySource.Branch, Default, null, null, null));
        Assert.Null(exception);
    }
}
