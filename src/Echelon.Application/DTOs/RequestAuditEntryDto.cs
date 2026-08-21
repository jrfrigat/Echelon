namespace Echelon.Application.DTOs;

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
