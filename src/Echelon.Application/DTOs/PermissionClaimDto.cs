namespace Echelon.Application.DTOs;

/// <summary>A permission claim a group or a user can hold.</summary>
/// <param name="Id">The claim id.</param>
/// <param name="Name">Its name, as the authorization policies name it.</param>
public record PermissionClaimDto(Guid Id, string Name);
