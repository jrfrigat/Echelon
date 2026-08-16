using Microsoft.Extensions.Options;
using Echelon.Application.DTOs;
using Echelon.Infrastructure.Audit;
using Xunit;

namespace Echelon.UnitTests.Audit;

/// <summary>
/// Covers the buffer's two jobs: never harming the request that feeds it, and never letting an
/// anonymous caller decide how much this service writes.
/// </summary>
public class RequestAuditBufferTests
{
    private static readonly DateTime Now = new(2026, 7, 20, 12, 0, 0, DateTimeKind.Utc);

    private static RequestAuditBuffer Buffer(int capacity = 1000, int anonymousPerMinute = 600) =>
        new(Options.Create(new RequestAuditOptions
        {
            BufferCapacity = capacity,
            MaxAnonymousRecordsPerMinute = anonymousPerMinute
        }));

    private static RequestAuditRecord Record(string? userId = null, int status = 200, string method = "GET") =>
        new("core", "host", method, "api/tasks", "/api/tasks", RequestAuditKinds.Api,
            status, 5, Now, userId, null, null, null, null, "corr", null);

    [Fact]
    public void AcceptsRecordsUpToCapacity()
    {
        var buffer = Buffer(capacity: 64);

        for (var i = 0; i < 64; i++)
            buffer.Enqueue(Record(userId: "u"));

        Assert.Equal(0, buffer.Dropped);
    }

    /// <summary>
    /// A full buffer drops and counts. It must never block: blocking here would make every user's
    /// request wait on the audit, which trades the thing being observed for the observation.
    /// </summary>
    [Fact]
    public void DropsAndCountsWhenFull_WithoutBlockingOrThrowing()
    {
        var buffer = Buffer(capacity: 16);

        var exception = Xunit.Record.Exception(() =>
        {
            for (var i = 0; i < 200; i++)
                buffer.Enqueue(Record(userId: "u"));
        });

        Assert.Null(exception);
        Assert.True(buffer.Dropped > 0);
    }

    /// <summary>
    /// The single most important test here. An unauthenticated stranger can issue requests as fast as
    /// they like; past the per-minute ceiling each one must cost a counter increment, not a row plus
    /// three index entries. Without this the audit table is a denial-of-service amplifier.
    /// </summary>
    [Fact]
    public void CapsAnonymousRecordsPerMinute()
    {
        var buffer = Buffer(capacity: 10_000, anonymousPerMinute: 10);

        for (var i = 0; i < 100; i++)
            buffer.Enqueue(Record(userId: null));

        var stored = Drain(buffer);

        Assert.Equal(10, stored);
        Assert.Equal(90, buffer.Dropped);
        Assert.True(buffer.AnonymousCapBound);
    }

    /// <summary>The cap applies to anonymous traffic only: a signed-in operator is never throttled out of the audit.</summary>
    [Fact]
    public void DoesNotCapAuthenticatedRecords()
    {
        var buffer = Buffer(capacity: 10_000, anonymousPerMinute: 10);

        for (var i = 0; i < 100; i++)
            buffer.Enqueue(Record(userId: "8f2c0000-0000-0000-0000-000000000a91"));

        Assert.Equal(100, Drain(buffer));
        Assert.Equal(0, buffer.Dropped);
        Assert.False(buffer.AnonymousCapBound);
    }

    [Fact]
    public void AnonymousCapOfZeroRejectsEverythingAnonymous()
    {
        var buffer = Buffer(anonymousPerMinute: 0);

        buffer.Enqueue(Record(userId: null));

        Assert.Equal(0, Drain(buffer));
        Assert.Equal(1, buffer.Dropped);
    }

    /// <summary>Notability is what the retention tiers and the default view both key on, so it is worth pinning.</summary>
    [Theory]
    [InlineData("GET", 200, false)]
    [InlineData("GET", 404, true)]
    [InlineData("GET", 500, true)]
    [InlineData("POST", 200, true)]
    [InlineData("DELETE", 204, true)]
    public void NotabilityCoversFailuresAndStateChanges(string method, int status, bool expected) =>
        Assert.Equal(expected, Record(status: status, method: method).IsNotable);

    private static int Drain(RequestAuditBuffer buffer)
    {
        var count = 0;
        while (buffer.Reader.TryRead(out _)) count++;
        return count;
    }
}
