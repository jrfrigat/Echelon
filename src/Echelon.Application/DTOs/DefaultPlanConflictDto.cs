namespace Echelon.Application.DTOs;

/// <summary>An ordering rule the default plan could not honour - in practice, a cycle in the rules.</summary>
public record DefaultPlanConflictDto(string FromRepositoryName, string ToRepositoryName, string Kind, string Reason);
