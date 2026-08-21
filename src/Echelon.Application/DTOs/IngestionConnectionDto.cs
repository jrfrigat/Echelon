using Echelon.Core.Enums;

namespace Echelon.Application.DTOs;

/// <summary>What the last poll of one connection did.</summary>
/// <param name="Kind">Which side it sits on.</param>
/// <param name="Name">The connection's name.</param>
/// <param name="LastPolledAt">When it was last polled from this replica, UTC.</param>
/// <param name="Emitted">What that poll produced: merge requests, or task syncs.</param>
/// <param name="Extra">The second count the poll reports - branches seen, or tasks newly discovered.</param>
/// <param name="Failure">Why it could not be read, when it could not.</param>
public record IngestionConnectionDto(
    IngestionConnectionKind Kind,
    string Name,
    DateTime LastPolledAt,
    int Emitted,
    int Extra,
    string? Failure);
