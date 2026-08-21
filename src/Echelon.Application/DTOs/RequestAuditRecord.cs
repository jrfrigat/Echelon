namespace Echelon.Application.DTOs;

/// <summary>
/// A completed request, reduced to primitives so it can cross from the web host into the buffer
/// without carrying an ASP.NET type with it.
/// </summary>
/// <param name="Host">Which host served it.</param>
/// <param name="Instance">Which replica served it.</param>
/// <param name="Method">HTTP method.</param>
/// <param name="RoutePattern">The matched route template - the aggregation key, never the raw path.</param>
/// <param name="Path">The path, never including a query string.</param>
/// <param name="Kind">One of <see cref="RequestAuditKinds"/>.</param>
/// <param name="StatusCode">The status the caller received.</param>
/// <param name="DurationMs">Elapsed milliseconds.</param>
/// <param name="StartedAt">When it began, UTC.</param>
/// <param name="UserId">The caller's object id, when authenticated.</param>
/// <param name="UserName">The caller's display name.</param>
/// <param name="PeerIp">The transport peer - unforgeable.</param>
/// <param name="ForwardedIp">The forwarded-header address - caller-supplied, not verified.</param>
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
    /// True for failures and for anything that changed state - the entries an operator actually
    /// wants, and the ones kept longest.
    /// </summary>
    public bool IsNotable =>
        StatusCode >= 400
        || !string.Equals(Method, "GET", StringComparison.OrdinalIgnoreCase);
}
