using System.Net.Http.Json;
using Echelon.Application.DTOs;

namespace Echelon.Pwa.Services.Api;

/// <summary>Who holds which permission.</summary>
/// <param name="http">The client this area talks over.</param>
public sealed class PermissionsApi(HttpClient http) : ApiClient(http)
{
    public Task<List<PermissionClaimDto>> GetPermissionClaimsAsync(CancellationToken ct = default) =>
        GetAsync<List<PermissionClaimDto>>("api/permissions/claims", ct);

    public Task<List<GroupMappingDto>> GetGroupMappingsAsync(CancellationToken ct = default) =>
        GetAsync<List<GroupMappingDto>>("api/permissions/group-mappings", ct);

    public Task AddGroupMappingAsync(string adGroupSid, Guid permissionClaimId, CancellationToken ct = default) =>
        SendAsync(() => Http.PostAsJsonAsync("api/permissions/group-mappings",
            new { AdGroupSid = adGroupSid, PermissionClaimId = permissionClaimId }, Json, ct), ct);

    public Task RemoveGroupMappingAsync(Guid id, CancellationToken ct = default) =>
        SendAsync(() => Http.DeleteAsync($"api/permissions/group-mappings/{id}", ct), ct);

    public Task AddUserOverrideAsync(string userId, Guid permissionClaimId, CancellationToken ct = default) =>
        SendAsync(() => Http.PostAsJsonAsync("api/permissions/user-overrides",
            new { UserId = userId, PermissionClaimId = permissionClaimId }, Json, ct), ct);
}
