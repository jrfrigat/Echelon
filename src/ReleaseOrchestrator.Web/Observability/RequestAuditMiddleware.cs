using System.Diagnostics;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ReleaseOrchestrator.Application.DTOs;
using ReleaseOrchestrator.Application.Services;

namespace ReleaseOrchestrator.Observability;

// WHAT THIS DELIBERATELY NEVER READS: request or response headers, request or response bodies, query
// strings, cookies. Not filtered -- never read. The sign-in endpoint receives a cleartext username
// and password, every call from the app carries an Authorization bearer token, and the webhook host
// carries provider signing tokens. Because none of it is read, there is no allowlist to
// misconfigure and no endpoint added later that leaks by being forgotten. Anyone adding "just the
// headers, for debugging" is removing that guarantee and should have to argue for it here.

/// <summary>
/// Records one row per meaningful request: what was called, by whom, what came back, how long.
/// </summary>
/// <remarks>
/// <para>
/// Must be registered FIRST, above forwarded-header handling. Three things depend on that position,
/// and all three break quietly if it moves:
/// </para>
/// <list type="number">
///   <item>
///     The transport peer is captured on the way in, before <c>X-Forwarded-For</c> has rewritten the
///     connection's address. That header is accepted from any caller in this deployment, so the
///     rewritten value is whatever the client claimed — the peer address is the only one that is not.
///   </item>
///   <item>
///     Being upstream of the exception handler means the status read on the way out is the one the
///     handler wrote, not the exception that briefly passed through. Register this below it and
///     every domain failure records 200 or 500.
///   </item>
///   <item>
///     Authentication runs downstream and mutates the same context, so the principal is populated by
///     the time this reads it on the way out — which is why the user is read AFTER the pipeline
///     returns and not before. A reviewer tidying that up would silently blank every user id.
///   </item>
/// </list>
/// </remarks>
public static class RequestAuditMiddleware
{
    /// <summary>Stored in place of the real path when a request reached no endpoint. Cardinality: one.</summary>
    private const string RoutingMissPath = "(routing miss)";

    /// <summary>Stored in place of the route pattern for the same requests, for the same reason.</summary>
    private const string RoutingMissPattern = "(no route)";

    /// <summary>
    /// Adds request auditing. Register before every other middleware.
    /// </summary>
    /// <param name="app">The application builder.</param>
    /// <param name="hostName">Which host this is, recorded on every row.</param>
    /// <returns>The same builder, for chaining.</returns>
    public static IApplicationBuilder UseRequestAudit(this IApplicationBuilder app, string hostName)
    {
        ArgumentNullException.ThrowIfNull(app);

        return app.Use(async (context, next) =>
        {
            var sink = context.RequestServices.GetService<IRequestAuditSink>();
            if (sink is null)
            {
                // Not configured in this host. Nothing to do, and nothing to fail.
                await next();
                return;
            }

            // The privacy switches are read here, where the values are collected. They were
            // documented and shipped in appsettings.json while nothing consulted them -- a setting
            // that reads as honoured and is not is worse than no setting, because an operator who
            // turns off IP recording believes they have.
            var privacy = context.RequestServices.GetService<IOptions<RequestAuditPrivacy>>()?.Value
                          ?? RequestAuditPrivacy.RecordEverything;

            // Before UseForwardedHeaders rewrites it. This is who actually connected.
            var peerIp = context.Connection.RemoteIpAddress?.ToString();

            // Captured on the way in for the same reason, and it is not optional: MapFallbackToFile
            // REWRITES Request.Path to "/index.html" before serving the app shell. Read on the way
            // out, every unmatched path -- every probe, every stale client route -- reads as
            // "/index.html" and is dropped as a static asset. That is what made API probes invisible
            // until end-to-end testing caught it.
            var originalPath = context.Request.Path;

            // The injected clock where one is registered — both hosts register it — falling back to
            // the system clock rather than failing: this middleware must never be the reason a
            // request does not serve. Only the recorded StartedAt uses it; the duration below is a
            // Stopwatch, which is monotonic and must not be replaced by clock arithmetic.
            var clock = context.RequestServices.GetService<TimeProvider>() ?? TimeProvider.System;

            var startedAt = clock.GetUtcNow().UtcDateTime;
            var startTimestamp = Stopwatch.GetTimestamp();

            try
            {
                await next();
            }
            finally
            {
                // Everything about recording is inside a catch-all. A throw here happens after the
                // response has begun, so it cannot become a clean 500 -- it presents to the caller as
                // a truncated response. The audit must never be able to damage the request it audits.
                try
                {
                    Record(context, sink, hostName, peerIp, originalPath, startedAt, startTimestamp, privacy);
                }
                catch
                {
                    // Intentionally swallowed. There is nowhere safe to report from here, and a
                    // failure to observe must not become a failure to serve.
                }
            }
        });
    }

    private static void Record(
        HttpContext context,
        IRequestAuditSink sink,
        string hostName,
        string? peerIp,
        PathString originalPath,
        DateTime startedAt,
        long startTimestamp,
        RequestAuditPrivacy privacy)
    {
        if (TelemetryExtensions.IsInfrastructureRequest(originalPath)) return;

        var endpoint = context.GetEndpoint();
        var path = originalPath.Value ?? "/";
        var routePattern = (endpoint as RouteEndpoint)?.RoutePattern.RawText ?? endpoint?.DisplayName;

        // "Reached no real endpoint" covers two shapes that look different and mean the same thing:
        // no endpoint was matched at all, or the SPA catch-all matched and served the app shell. Both
        // answer 200 for anything, so both are treated identically -- keyed off the outcome rather
        // than off which of the two the framework happened to produce, because that turned out to
        // vary and a rule that depends on it silently records nothing.
        if (routePattern is null || IsSpaFallback(routePattern))
        {
            // An API-shaped path that reached no endpoint is a real signal: a probe, a typo, or a
            // client calling a route that no longer exists. Anything else is somebody deep-linking
            // into the app, which is not worth a row.
            if (!IsApiShaped(path)) return;

            // The path is replaced by a fixed literal and the pattern by a constant. This is the
            // whole anonymous-write-amplification fix: the fallback answers 200 for any
            // extension-less path, so without it an unauthenticated stranger could mint unlimited
            // distinct Path values -- unbounded rows and unbounded index cardinality, chosen
            // entirely by them. Collapsed this way, a flood of junk URLs is one row per minute.
            Enqueue(context, sink, hostName, peerIp, startedAt, startTimestamp,
                RoutingMissPattern, RoutingMissPath, RequestAuditKinds.RoutingMiss, endpoint, privacy);
            return;
        }

        var kind = ClassifyPath(originalPath);
        Enqueue(context, sink, hostName, peerIp, startedAt, startTimestamp, routePattern, path, kind, endpoint, privacy);
    }

    private static void Enqueue(
        HttpContext context,
        IRequestAuditSink sink,
        string hostName,
        string? peerIp,
        DateTime startedAt,
        long startTimestamp,
        string routePattern,
        string path,
        string kind,
        Endpoint? endpoint,
        RequestAuditPrivacy privacy)
    {

        var elapsed = Stopwatch.GetElapsedTime(startTimestamp);

        sink.Enqueue(new RequestAuditRecord(
            Host: hostName,
            Instance: Environment.MachineName,
            // Clamped like every other caller-controlled string, and this one is not cosmetic: the
            // column is 10 characters and HTTP methods are an open token set -- WebDAV alone has
            // VERSION-CONTROL (15) and BASELINE-CONTROL (16), and a scanner can send anything. An
            // over-long value makes SaveChanges throw, and the writer's catch discards the WHOLE
            // batch of up to 200 mostly-unrelated records while the summary still reports zero
            // dropped. An unauthenticated caller could hold the audit dark indefinitely. SQLite does
            // not enforce length, so no test here could have seen it.
            Method: Truncate(context.Request.Method, 10) ?? "?",
            RoutePattern: Truncate(routePattern, 200) ?? "unknown",
            Path: Truncate(path, 300) ?? "/",
            Kind: kind,
            StatusCode: context.Response.StatusCode,
            DurationMs: (int)Math.Min(int.MaxValue, elapsed.TotalMilliseconds),
            StartedAt: startedAt,
            UserId: ResolveUserId(context.User),
            UserName: privacy.RecordUserName && context.User.Identity?.IsAuthenticated == true
                ? Truncate(context.User.FindFirstValue("preferred_username") ?? context.User.Identity.Name, 256)
                : null,
            PeerIp: privacy.RecordClientIp ? Truncate(peerIp, 64) : null,
            // Read now, after the pipeline: this is the forwarded-header value, which is
            // caller-supplied and labelled as such wherever it is shown.
            ForwardedIp: privacy.RecordClientIp ? Truncate(context.Connection.RemoteIpAddress?.ToString(), 64) : null,
            Permission: Truncate(endpoint?.Metadata.GetMetadata<IAuthorizeData>()?.Policy, 200),
            CorrelationId: Truncate(context.TraceIdentifier, 64) ?? string.Empty,
            // The TYPE only. A database driver's exception message can carry a connection string; the
            // full text stays in the log, reachable by the correlation id.
            ExceptionType: Truncate(context.Features.Get<IExceptionHandlerFeature>()?.Error.GetType().Name, 200)));
    }

    /// <summary>True for the catch-all route that serves the app shell for client-side routes.</summary>
    private static bool IsSpaFallback(string routePattern) =>
        routePattern.Contains("{*path", StringComparison.OrdinalIgnoreCase)
        || routePattern.Contains("{**", StringComparison.Ordinal);

    /// <summary>
    /// True for a path that was clearly meant to reach the API, so a miss on it is a real signal
    /// rather than someone deep-linking into the app.
    /// </summary>
    private static bool IsApiShaped(string path) =>
        path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase)
        || path.StartsWith("/auth/", StringComparison.OrdinalIgnoreCase);

    private static string ClassifyPath(PathString path)
    {
        if (path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase)) return RequestAuditKinds.Api;
        if (path.StartsWithSegments("/auth", StringComparison.OrdinalIgnoreCase)) return RequestAuditKinds.Auth;
        return RequestAuditKinds.Other;
    }

    /// <summary>
    /// The caller's object id, normalized the same way permissions are keyed.
    /// </summary>
    /// <remarks>
    /// Deliberately duplicated rather than calling Infrastructure's resolver: this file is compiled
    /// into the webhook host too, which must not gain a dependency on Infrastructure. The claim names
    /// are a stable part of the token format, not this codebase's invention.
    /// </remarks>
    private static string? ResolveUserId(ClaimsPrincipal user)
    {
        if (user.Identity?.IsAuthenticated != true) return null;

        var raw = user.FindFirstValue("oid")
                  ?? user.FindFirstValue("http://schemas.microsoft.com/identity/claims/objectidentifier");

        return Guid.TryParse(raw, out var oid) && oid != Guid.Empty ? oid.ToString("D") : null;
    }

    private static string? Truncate(string? value, int max) =>
        value is null ? null : value.Length <= max ? value : value[..max];
}
