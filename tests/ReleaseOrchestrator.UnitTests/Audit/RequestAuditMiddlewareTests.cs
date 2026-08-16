using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.Extensions.DependencyInjection;
using ReleaseOrchestrator.Application.DTOs;
using ReleaseOrchestrator.Application.Services;
using ReleaseOrchestrator.Observability;
using Xunit;

namespace ReleaseOrchestrator.UnitTests.Audit;

/// <summary>
/// Covers what the recorder stores and, more importantly, what it refuses to.
/// </summary>
/// <remarks>
/// NOT covered: the middleware's ORDER in the real pipeline. Its correctness depends on running
/// above forwarded headers, above the exception handler and above authentication, and this repo has
/// no integration host to assert that with. The comment on the registration line is what protects
/// it; these tests protect everything downstream of the position being right.
/// </remarks>
public class RequestAuditMiddlewareTests
{
    private sealed class CapturingSink : IRequestAuditSink
    {
        public List<RequestAuditRecord> Records { get; } = [];
        public void Enqueue(RequestAuditRecord record) => Records.Add(record);
    }

    private sealed class ThrowingSink : IRequestAuditSink
    {
        public void Enqueue(RequestAuditRecord record) => throw new InvalidOperationException("sink is broken");
    }

    private static async Task<List<RequestAuditRecord>> RunAsync(
        Action<HttpContext> arrange,
        RequestDelegate? terminal = null,
        IRequestAuditSink? sink = null)
    {
        var capturing = sink ?? new CapturingSink();

        var services = new ServiceCollection();
        services.AddSingleton(capturing);
        var provider = services.BuildServiceProvider();

        var builder = new ApplicationBuilder(provider);
        builder.UseRequestAudit("core");
        builder.Run(terminal ?? (_ => Task.CompletedTask));
        var pipeline = builder.Build();

        var context = new DefaultHttpContext { RequestServices = provider };
        arrange(context);

        await pipeline(context);

        return capturing is CapturingSink c ? c.Records : [];
    }

    private static Endpoint RouteEndpointFor(string pattern) =>
        new RouteEndpoint(
            _ => Task.CompletedTask,
            RoutePatternFactory.Parse(pattern),
            order: 0,
            new EndpointMetadataCollection(),
            displayName: pattern);

    // ---- what it records ------------------------------------------------------

    /// <summary>
    /// The central claim: the status recorded is the one the caller received, not the one the request
    /// started with. This is as far as a unit test can express it - the rest depends on pipeline order.
    /// </summary>
    [Fact]
    public async Task RecordsTheFinalStatus_NotTheInboundOne()
    {
        var records = await RunAsync(
            ctx =>
            {
                ctx.SetEndpoint(RouteEndpointFor("api/tasks/{id}"));
                ctx.Request.Path = "/api/tasks/1";
            },
            terminal: ctx =>
            {
                ctx.Response.StatusCode = 404;
                return Task.CompletedTask;
            });

        var record = Assert.Single(records);
        Assert.Equal(404, record.StatusCode);
        Assert.Equal("api/tasks/{id}", record.RoutePattern);
    }

    /// <summary>
    /// The peer is captured on the way in, before forwarded headers rewrite the address; the
    /// forwarded value is read afterwards. This deployment accepts X-Forwarded-For from anyone, so
    /// conflating the two would record an attacker-chosen string as the caller.
    /// </summary>
    [Fact]
    public async Task DistinguishesTheTransportPeerFromTheForwardedAddress()
    {
        var records = await RunAsync(
            ctx =>
            {
                ctx.SetEndpoint(RouteEndpointFor("api/tasks"));
                ctx.Request.Path = "/api/tasks";
                ctx.Connection.RemoteIpAddress = IPAddress.Parse("10.0.0.1");
            },
            terminal: ctx =>
            {
                // Stand-in for UseForwardedHeaders running downstream and rewriting the address.
                ctx.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.9");
                return Task.CompletedTask;
            });

        var record = Assert.Single(records);
        Assert.Equal("10.0.0.1", record.PeerIp);
        Assert.Equal("203.0.113.9", record.ForwardedIp);
    }

    [Fact]
    public async Task RecordsTheExceptionTypeButNeverItsMessage()
    {
        const string secret = "Server=db;Password=hunter2";

        var records = await RunAsync(
            ctx =>
            {
                ctx.SetEndpoint(RouteEndpointFor("api/tasks"));
                ctx.Request.Path = "/api/tasks";
            },
            terminal: ctx =>
            {
                ctx.Features.Set<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>(
                    new Microsoft.AspNetCore.Diagnostics.ExceptionHandlerFeature
                    {
                        Error = new InvalidOperationException(secret)
                    });
                ctx.Response.StatusCode = 500;
                return Task.CompletedTask;
            });

        var record = Assert.Single(records);
        Assert.Equal(nameof(InvalidOperationException), record.ExceptionType);

        // A driver's exception message can carry a connection string. The type classifies the
        // failure; the text stays in the log, reachable by the correlation id.
        Assert.DoesNotContain(secret, string.Join('|', record.ToString()));
    }

    // ---- what it refuses to record --------------------------------------------

    /// <summary>
    /// The regression guard for the whole secrets posture. These values are never read, so no
    /// allowlist can be misconfigured - this test is what keeps it that way.
    /// </summary>
    [Fact]
    public async Task NeverStoresQueryStringsHeadersOrCookies()
    {
        const string token = "Bearer super-secret-token";
        const string queryValue = "secret-query-value";

        var records = await RunAsync(ctx =>
        {
            ctx.SetEndpoint(RouteEndpointFor("api/tasks"));
            ctx.Request.Path = "/api/tasks";
            ctx.Request.QueryString = new QueryString($"?apiKey={queryValue}");
            ctx.Request.Headers.Authorization = token;
            ctx.Request.Headers["X-Gitlab-Token"] = "webhook-signing-token";
            ctx.Request.Headers.Cookie = "session=abc";
        });

        var record = Assert.Single(records);
        var serialized = string.Join('|',
            record.Path, record.RoutePattern, record.Kind, record.CorrelationId,
            record.UserId, record.UserName, record.PeerIp, record.ForwardedIp,
            record.Permission, record.ExceptionType);

        Assert.DoesNotContain(queryValue, serialized);
        Assert.DoesNotContain("super-secret-token", serialized);
        Assert.DoesNotContain("webhook-signing-token", serialized);
        Assert.DoesNotContain("session=abc", serialized);
    }

    [Fact]
    public async Task IgnoresStaticAssetsAndInfrastructurePaths()
    {
        // No endpoint and not API-shaped: a static file. A cold page load fetches hundreds of these.
        var asset = await RunAsync(ctx => ctx.Request.Path = "/_framework/blazor.boot.json");
        Assert.Empty(asset);

        var health = await RunAsync(ctx =>
        {
            ctx.SetEndpoint(RouteEndpointFor("health"));
            ctx.Request.Path = "/health";
        });
        Assert.Empty(health);

        var metrics = await RunAsync(ctx =>
        {
            ctx.SetEndpoint(RouteEndpointFor("metrics"));
            ctx.Request.Path = "/metrics";
        });
        Assert.Empty(metrics);
    }

    /// <summary>
    /// The anonymous write-amplification fix. The SPA fallback answers 200 for any extension-less
    /// path, so a stranger could otherwise mint unlimited distinct Path values. An API-shaped miss is
    /// recorded with a fixed literal (cardinality 1); a genuine deep link is not recorded at all.
    /// </summary>
    [Fact]
    public async Task ARoutingMissStoresNoAttackerControlledString()
    {
        var records = await RunAsync(ctx =>
        {
            ctx.SetEndpoint(RouteEndpointFor("{*path:nonfile}"));
            ctx.Request.Path = "/api/../../etc/passwd-or-any-junk-they-like";
        });

        var record = Assert.Single(records);
        Assert.Equal(RequestAuditKinds.RoutingMiss, record.Kind);
        Assert.Equal("(routing miss)", record.Path);
        Assert.DoesNotContain("passwd", record.Path);
    }

    [Fact]
    public async Task ARealDeepLinkIntoTheAppIsNotRecorded()
    {
        var records = await RunAsync(ctx =>
        {
            ctx.SetEndpoint(RouteEndpointFor("{*path:nonfile}"));
            ctx.Request.Path = "/rollouts/8f2c0000-0000-0000-0000-000000000a91";
        });

        Assert.Empty(records);
    }

    /// <summary>
    /// The same treatment when NO endpoint is exposed at all, which is what actually happens in the
    /// running app: an unmatched API path is served the app shell with a 200, and end-to-end testing
    /// showed the fallback endpoint is not visible here. Keying the rule off the outcome rather than
    /// off which shape the framework produced is what stopped this recording nothing.
    /// </summary>
    [Fact]
    public async Task AnApiPathThatReachedNoEndpointIsStillRecordedAsAMiss()
    {
        var records = await RunAsync(ctx => ctx.Request.Path = "/api/does-not-exist-abcdef");

        var record = Assert.Single(records);
        Assert.Equal(RequestAuditKinds.RoutingMiss, record.Kind);
        Assert.Equal("(routing miss)", record.Path);
        Assert.Equal("(no route)", record.RoutePattern);
        Assert.DoesNotContain("does-not-exist", record.Path);
    }

    /// <summary>
    /// The path must be judged on what was requested, not on what the pipeline rewrote it to.
    /// </summary>
    /// <remarks>
    /// MapFallbackToFile rewrites Request.Path to "/index.html" before serving the app shell. Reading
    /// the path on the way out therefore sees "/index.html" for every unmatched route, which is not
    /// API-shaped and gets dropped - so every probe was invisible until this was caught end to end.
    /// The middleware captures the path on the way in; this pins that it still does.
    /// </remarks>
    [Fact]
    public async Task JudgesTheRequestedPath_NotOneRewrittenDuringTheRequest()
    {
        var records = await RunAsync(
            ctx => ctx.Request.Path = "/api/probe-that-matches-nothing",
            terminal: ctx =>
            {
                // Stand-in for MapFallbackToFile serving the shell.
                ctx.Request.Path = "/index.html";
                ctx.Response.StatusCode = 200;
                return Task.CompletedTask;
            });

        var record = Assert.Single(records);
        Assert.Equal(RequestAuditKinds.RoutingMiss, record.Kind);
    }

    /// <summary>
    /// Cardinality is the point: a thousand distinct probed URLs must collapse to one route pattern
    /// and one path, or the table and its indexes grow at a rate an anonymous caller chooses.
    /// </summary>
    [Fact]
    public async Task AFloodOfDistinctProbedPathsCollapsesToOneKey()
    {
        var sink = new CapturingSink();

        for (var i = 0; i < 50; i++)
            await RunAsync(ctx => ctx.Request.Path = $"/api/probe-{Guid.NewGuid():N}", sink: sink);

        Assert.Equal(50, sink.Records.Count);
        Assert.Single(sink.Records.Select(r => r.Path).Distinct());
        Assert.Single(sink.Records.Select(r => r.RoutePattern).Distinct());
    }

    // ---- it must never harm the request ---------------------------------------

    [Fact]
    public async Task AFailingSinkNeverSurfacesToTheCaller()
    {
        var exception = await Record.ExceptionAsync(() => RunAsync(
            ctx =>
            {
                ctx.SetEndpoint(RouteEndpointFor("api/tasks"));
                ctx.Request.Path = "/api/tasks";
            },
            sink: new ThrowingSink()));

        // A throw while recording happens after the response has begun, so it cannot become a clean
        // 500 -- it reaches the caller as a truncated response. Failing to observe must never become
        // failing to serve.
        Assert.Null(exception);
    }

    [Fact]
    public async Task AnExceptionFromThePipelinePropagatesUnchanged()
    {
        var exception = await Record.ExceptionAsync(() => RunAsync(
            ctx =>
            {
                ctx.SetEndpoint(RouteEndpointFor("api/tasks"));
                ctx.Request.Path = "/api/tasks";
            },
            terminal: _ => throw new InvalidOperationException("boom")));

        Assert.IsType<InvalidOperationException>(exception);
        Assert.Equal("boom", exception!.Message);
    }

    /// <summary>
    /// Every caller-controlled string must fit its column, and the method is the one that nearly did
    /// not.
    /// </summary>
    /// <remarks>
    /// HTTP methods are an open token set - WebDAV alone has VERSION-CONTROL (15) and
    /// BASELINE-CONTROL (16) - while the column is 10 characters. An over-long value made
    /// SaveChanges throw, and the writer's catch discarded the whole batch of up to 200 mostly
    /// unrelated records while the summary still reported zero dropped: an anonymous caller could
    /// hold the audit dark indefinitely. SQLite does not enforce length, so only an assertion here
    /// can catch it.
    /// </remarks>
    [Fact]
    public async Task ClampsEveryCallerControlledFieldToItsColumnWidth()
    {
        var records = await RunAsync(ctx =>
        {
            ctx.SetEndpoint(RouteEndpointFor("api/tasks"));
            ctx.Request.Path = "/api/tasks";
            ctx.Request.Method = "VERSION-CONTROL";   // 15 chars against a 10-char column
        });

        var record = Assert.Single(records);
        Assert.True(record.Method.Length <= 10, $"Method '{record.Method}' exceeds its column width");
        Assert.True(record.RoutePattern.Length <= 200);
        Assert.True(record.Path.Length <= 300);
        Assert.True((record.UserName?.Length ?? 0) <= 256);
        Assert.True((record.PeerIp?.Length ?? 0) <= 64);
        Assert.True((record.ExceptionType?.Length ?? 0) <= 200);
    }

    [Fact]
    public async Task RecordsTheAuthenticatedCaller()
    {
        var oid = "8f2c0000-0000-0000-0000-000000000a91";

        var records = await RunAsync(ctx =>
        {
            ctx.SetEndpoint(RouteEndpointFor("api/tasks"));
            ctx.Request.Path = "/api/tasks";
            ctx.User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim("oid", oid), new Claim("preferred_username", "vera@example.com")],
                authenticationType: "test"));
        });

        var record = Assert.Single(records);
        Assert.Equal(oid, record.UserId);
        Assert.Equal("vera@example.com", record.UserName);
    }
}
