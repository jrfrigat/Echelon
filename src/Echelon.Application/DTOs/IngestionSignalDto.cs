using Echelon.Core.Enums;

namespace Echelon.Application.DTOs;

/// <summary>How much of one kind of change has arrived, and when the last one did.</summary>
/// <param name="Signal">The kind of change.</param>
/// <param name="Count">How many have arrived since this replica started.</param>
/// <param name="LastAt">When the last one arrived, UTC; null when none has.</param>
public record IngestionSignalDto(IngestionSignal Signal, long Count, DateTime? LastAt);
