using Echelon.Core.Enums;

namespace Echelon.Application.DTOs;

/// <summary>One background worker, as the operations screen shows it.</summary>
/// <param name="Worker">Which worker.</param>
/// <param name="State">Whether it is running, waiting, held by another replica, or switched off.</param>
/// <param name="IntervalSeconds">How often it wakes, as configured.</param>
/// <param name="LastStartedAt">When its last pass began, UTC; null when it has not run here yet.</param>
/// <param name="LastFinishedAt">When that pass ended, UTC; null while one is in flight.</param>
/// <param name="LastDurationMs">How long it took.</param>
/// <param name="Outcome">How it ended.</param>
/// <param name="Error">Why it failed, when it did. Kept because "failed" on its own is not actionable.</param>
/// <param name="Passes">Passes completed since this replica started.</param>
/// <param name="Emitted">What the last pass produced - tasks queued, merge requests seen, however the worker counts.</param>
public record IngestionWorkerDto(
    IngestionWorker Worker,
    IngestionRunState State,
    int IntervalSeconds,
    DateTime? LastStartedAt,
    DateTime? LastFinishedAt,
    int? LastDurationMs,
    IngestionOutcome Outcome,
    string? Error,
    long Passes,
    int Emitted);
