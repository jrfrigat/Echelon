namespace ReleaseOrchestrator.Application.DTOs;

/// <summary>
/// A completed request, reduced to primitives so it can cross from the web host into the buffer
/// without carrying an ASP.NET type with it.
/// </summary>
/// <param name="Host">Which host served it.</param>
/// <param name="Instance">Which replica served it.</param>
/// <param name="Method">HTTP method.</param>
/// <param name="RoutePattern">The matched route template — the aggregation key, never the raw path.</param>
/// <param name="Path">The path, never including a query string.</param>
/// <param name="Kind">One of <see cref="RequestAuditKinds"/>.</param>
/// <param name="StatusCode">The status the caller received.</param>
/// <param name="DurationMs">Elapsed milliseconds.</param>
/// <param name="StartedAt">When it began, UTC.</param>
/// <param name="UserId">The caller's object id, when authenticated.</param>
/// <param name="UserName">The caller's display name.</param>
/// <param name="PeerIp">The transport peer — unforgeable.</param>
/// <param name="ForwardedIp">The forwarded-header address — caller-supplied, not verified.</param>
/// <param name="Permission">The policy the endpoint required.</param>
/// <param name="CorrelationId">The id echoed to the caller and written to the log.</param>
/// <param name="ExceptionType">An unhandled exception's type name. Never its message.</param>
public record RequestAuditRecord(
    string Host,
    string Instance,
    string Method,
    string RoutePattern,
    string Path,
    string Kind,
    int StatusCode,
    int DurationMs,
    DateTime StartedAt,
    string? UserId,
    string? UserName,
    string? PeerIp,
    string? ForwardedIp,
    string? Permission,
    string CorrelationId,
    string? ExceptionType)
{
    /// <summary>
    /// True for failures and for anything that changed state — the entries an operator actually
    /// wants, and the ones kept longest.
    /// </summary>
    public bool IsNotable =>
        StatusCode >= 400
        || !string.Equals(Method, "GET", StringComparison.OrdinalIgnoreCase);
}

/// <summary>The vocabulary of <see cref="RequestAuditRecord.Kind"/>.</summary>
public static class RequestAuditKinds
{
    /// <summary>A call to the API.</summary>
    public const string Api = "Api";

    /// <summary>A sign-in or sign-out call.</summary>
    public const string Auth = "Auth";

    /// <summary>
    /// An API-shaped path that matched no endpoint and fell through to the app shell.
    /// </summary>
    /// <remarks>
    /// Distinguished because the fallback answers 200 for anything, so without this a mistyped or
    /// probed URL would be counted as a successful API call.
    /// </remarks>
    public const string RoutingMiss = "RoutingMiss";

    /// <summary>Anything else that matched an endpoint.</summary>
    public const string Other = "Other";
}

/// <summary>One row of the request log, as shown to an operator.</summary>
public record RequestAuditEntryDto(
    Guid Id,
    string Method,
    string RoutePattern,
    string Path,
    string Kind,
    int StatusCode,
    int DurationMs,
    DateTime StartedAt,
    string? UserId,
    string? UserName,
    string? PeerIp,
    string? ForwardedIp,
    string? Permission,
    string CorrelationId,
    string? ExceptionType,
    string Instance);

/// <summary>Traffic and latency for one endpoint over the window.</summary>
/// <param name="Method">HTTP method.</param>
/// <param name="RoutePattern">The endpoint.</param>
/// <param name="Count">Requests in the window.</param>
/// <param name="ClientErrors">4xx responses.</param>
/// <param name="ServerErrors">5xx responses.</param>
/// <param name="P50Ms">Median duration.</param>
/// <param name="P95Ms">95th percentile duration.</param>
/// <param name="MaxMs">Slowest request in the window.</param>
public record RequestAuditRouteStatDto(
    string Method,
    string RoutePattern,
    long Count,
    long ClientErrors,
    long ServerErrors,
    int P50Ms,
    int P95Ms,
    int MaxMs);

/// <summary>
/// The window at a glance, with its own limitations stated as data.
/// </summary>
/// <param name="FromUtc">Start of the window.</param>
/// <param name="ToUtc">End of the window.</param>
/// <param name="Total">Requests recorded in it.</param>
/// <param name="Errors">How many failed.</param>
/// <param name="Routes">Per-endpoint breakdown, worst first.</param>
/// <param name="PercentilesApproximate">
/// True when percentiles were computed from a capped sample rather than every row.
/// </param>
/// <param name="DroppedRecords">
/// Requests that happened but were not stored, because the buffer was full or the anonymous cap
/// bound. Surfaced so an operator never reads a gap as quiet traffic.
/// </param>
/// <param name="AnonymousCapBound">True when unauthenticated recording hit its per-minute ceiling.</param>
/// <param name="IngressCovered">
/// False while the webhook host is not recorded here. Stated rather than left to be discovered.
/// </param>
public record RequestAuditSummaryDto(
    DateTime FromUtc,
    DateTime ToUtc,
    long Total,
    long Errors,
    IReadOnlyList<RequestAuditRouteStatDto> Routes,
    bool PercentilesApproximate,
    long DroppedRecords,
    bool AnonymousCapBound,
    bool IngressCovered);
