using System.Text.Json;
using System.Text.Json.Serialization;
using Xunit;

namespace Echelon.UnitTests.Providers.YandexTracker;

/// <summary>
/// What Kind a tracker's resolution time arrives with, and why it decides whether closing a task
/// works on PostgreSQL.
/// </summary>
/// <remarks>
/// <para>
/// Mostly a test of System.Text.Json, deliberately. PostgreSQL maps DateTime to
/// <c>timestamp with time zone</c> and Npgsql writes only Kind=Utc, so what this parsing produces
/// is the difference between a task closing and an exception - on one of the two supported
/// databases and not the other. SQL Server takes every Kind without complaint, which is precisely
/// why the hazard is invisible until someone runs the other one.
/// </para>
/// <para>
/// The adapter therefore deserialises into <see cref="DateTimeOffset"/> and hands over
/// <c>UtcDateTime</c>. These pin the reason: an offset yields Kind=Local, and it is a library's
/// choice rather than ours, so a red test is the right way to hear about it changing.
/// </para>
/// </remarks>
public class ResolvedAtKindTests
{
    private sealed record AsDateTime([property: JsonPropertyName("resolvedAt")] DateTime? ResolvedAt);

    private sealed record AsOffset([property: JsonPropertyName("resolvedAt")] DateTimeOffset? ResolvedAt);

    private static string Json(string stamp) => $$"""{"resolvedAt":"{{stamp}}"}""";

    /// <summary>
    /// The hazard itself: an offset becomes a local time, which Npgsql refuses to write to
    /// timestamptz. This is what the adapter's DateTimeOffset avoids.
    /// </summary>
    [Fact]
    public void ParsedAsDateTimeAnOffsetBecomesALocalTime()
    {
        var parsed = JsonSerializer.Deserialize<AsDateTime>(Json("2026-07-17T15:00:00.000+03:00"))!.ResolvedAt;

        Assert.Equal(DateTimeKind.Local, parsed!.Value.Kind);
    }

    /// <summary>A stamp with no zone is worse: nothing knows what it meant.</summary>
    [Fact]
    public void ParsedAsDateTimeAStampWithNoZoneIsUnspecified()
    {
        var parsed = JsonSerializer.Deserialize<AsDateTime>(Json("2026-07-17T12:00:00.000"))!.ResolvedAt;

        Assert.Equal(DateTimeKind.Unspecified, parsed!.Value.Kind);
    }

    /// <summary>What the adapter does. Both shapes land on the same instant, tagged Utc.</summary>
    [Theory]
    [InlineData("2026-07-17T15:00:00.000+03:00")]
    [InlineData("2026-07-17T12:00:00.000Z")]
    [InlineData("2026-07-17T07:00:00.000-05:00")]
    public void ThroughDateTimeOffsetEveryZonedStampBecomesTheSameUtcInstant(string stamp)
    {
        var parsed = JsonSerializer.Deserialize<AsOffset>(Json(stamp))!.ResolvedAt;

        Assert.Equal(new DateTime(2026, 7, 17, 12, 0, 0, DateTimeKind.Utc), parsed!.Value.UtcDateTime);
        Assert.Equal(DateTimeKind.Utc, parsed.Value.UtcDateTime.Kind);
    }

    /// <summary>
    /// A basic-format offset - <c>+0000</c>, no colon - is not ISO-8601 as System.Text.Json reads
    /// it, and throws rather than parsing wrong. Recorded because it looks like the shape a tracker
    /// might send, and because the failure is a JsonException on the whole issue rather than a bad
    /// date: if a tracker ever sends it, this is what the log will say.
    /// </summary>
    [Fact]
    public void ABasicFormatOffsetIsRejectedOutright()
    {
        Assert.Throws<JsonException>(
            () => JsonSerializer.Deserialize<AsOffset>(Json("2026-07-17T12:00:00.000+0000")));
    }
}
