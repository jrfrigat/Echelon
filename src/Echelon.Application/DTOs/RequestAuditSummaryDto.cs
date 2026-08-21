namespace Echelon.Application.DTOs;

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
