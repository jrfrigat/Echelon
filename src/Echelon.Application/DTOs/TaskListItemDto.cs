namespace Echelon.Application.DTOs;

/// <summary>A task as it appears in the tasks list -- what an operator might roll out.</summary>
public record TaskListItemDto(
    Guid Id,
    string ExternalId,
    string Title,
    string Status,
    int MergeRequestCount,
    bool HasActivePlan);
