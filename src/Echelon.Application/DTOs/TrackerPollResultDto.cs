namespace Echelon.Application.DTOs;

/// <summary>What one tracker poll produced.</summary>
/// <param name="Emitted">Syncs requested, over the issues the tracker reported open and the tasks already known.</param>
/// <param name="Discovered">How many of those the tracker turned up that were not in the database yet.</param>
/// <param name="Failure">Why the tracker could not be searched; null when it was. The known tasks were still re-read.</param>
public record TrackerPollResultDto(int Emitted, int Discovered, string? Failure = null);
