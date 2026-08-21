namespace Echelon.Application.DTOs;

/// <summary>
/// Everything that happened to one task, in order.
/// </summary>
/// <param name="TaskId">The task.</param>
/// <param name="TaskExternalId">Its tracker key.</param>
/// <param name="IsArchived">True when the task itself has been archived; the entries then come from the archive.</param>
/// <param name="FirstSeenAt">When this service first stored it, or null when it predates arrival recording.</param>
/// <param name="FirstSeenSource">How it arrived, or null when the creating path knew of no source.</param>
/// <param name="Coverage">What this answer does NOT cover. Read it before trusting an absence.</param>
/// <param name="Entries">The events, newest first.</param>
public record TaskTimelineDto(
    Guid TaskId,
    string TaskExternalId,
    bool IsArchived,
    DateTime? FirstSeenAt,
    string? FirstSeenSource,
    TimelineCoverageDto Coverage,
    IReadOnlyList<TimelineEntryDto> Entries);
