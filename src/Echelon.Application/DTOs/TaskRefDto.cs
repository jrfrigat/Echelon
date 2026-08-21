namespace Echelon.Application.DTOs;

/// <summary>Another task named from this one - its parent, or one of its children.</summary>
public record TaskRefDto(Guid Id, string ExternalId, string Title);
