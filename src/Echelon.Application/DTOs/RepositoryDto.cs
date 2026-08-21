namespace Echelon.Application.DTOs;

/// <summary>A repository the service watches, and the connections that reach it.</summary>
/// <param name="Id">The repository id.</param>
/// <param name="Name">Display name.</param>
/// <param name="ExternalId">The provider's own id - for GitLab, the full <c>group/project</c> path.</param>
/// <param name="ConnectionId">The VCS connection that reads it.</param>
/// <param name="ConnectionName">That connection's name, so a list need not resolve it again.</param>
/// <param name="TrackerConnectionId">The tracker whose keys its branches carry, when one is set.</param>
/// <param name="TrackerConnectionName">That tracker's name, when one is set.</param>
public record RepositoryDto(
    Guid Id,
    string Name,
    string ExternalId,
    Guid ConnectionId,
    string ConnectionName,
    Guid? TrackerConnectionId = null,
    string? TrackerConnectionName = null);
