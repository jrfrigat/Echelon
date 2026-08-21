namespace Echelon.Application.DTOs;

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
