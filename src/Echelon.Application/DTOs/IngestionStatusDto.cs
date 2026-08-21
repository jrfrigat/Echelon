namespace Echelon.Application.DTOs;

/// <summary>
/// What the ingestion side of this replica is doing.
/// </summary>
/// <remarks>
/// Per replica and since it started, deliberately: this answers "is anything reading the tracker and
/// the VCS right now", which is a question about a running process. Nothing here is persisted, so a
/// restart clears it - that is the honest reading, since the counters describe this process's work.
/// </remarks>
/// <param name="ServerTimeUtc">The server's own clock, so the page can age everything against it rather than against the browser's.</param>
/// <param name="StartedAt">When this replica started recording, UTC.</param>
/// <param name="Workers">The background workers.</param>
/// <param name="Signals">What has arrived, by kind - webhooks and polls alike.</param>
/// <param name="Connections">The last poll of each connection.</param>
public record IngestionStatusDto(
    DateTime ServerTimeUtc,
    DateTime StartedAt,
    IReadOnlyList<IngestionWorkerDto> Workers,
    IReadOnlyList<IngestionSignalDto> Signals,
    IReadOnlyList<IngestionConnectionDto> Connections);
