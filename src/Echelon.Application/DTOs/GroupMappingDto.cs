namespace Echelon.Application.DTOs;

/// <summary>A directory group mapped to a claim, so its members inherit the permission.</summary>
/// <param name="Id">The mapping id.</param>
/// <param name="AdGroupSid">The group's security identifier.</param>
/// <param name="ClaimName">The claim its members are granted.</param>
public record GroupMappingDto(Guid Id, string AdGroupSid, string ClaimName);
