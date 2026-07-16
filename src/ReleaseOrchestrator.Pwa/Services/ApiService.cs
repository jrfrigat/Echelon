using System.Net.Http.Json;
using ReleaseOrchestrator.Pwa.Models;

namespace ReleaseOrchestrator.Pwa.Services;

public class ApiService(HttpClient http)
{
    // Release Plans
    public Task<ReleasePlanDto?> GetActivePlanAsync() =>
        http.GetFromJsonAsync<ReleasePlanDto>("api/release-plans/active");

    public Task<ReleasePlanDto?> GetPlanByIdAsync(Guid id) =>
        http.GetFromJsonAsync<ReleasePlanDto>($"api/release-plans/{id}");

    public async Task<ReleasePlanDto?> RecalculatePlanAsync()
    {
        var r = await http.PostAsync("api/release-plans/recalculate", null);
        r.EnsureSuccessStatusCode();
        return await r.Content.ReadFromJsonAsync<ReleasePlanDto>();
    }

    public async Task<string?> ExportYamlAsync(Guid planId)
    {
        var r = await http.GetAsync($"api/release-plans/{planId}/export");
        if (!r.IsSuccessStatusCode) return null;
        return await r.Content.ReadAsStringAsync();
    }

    public async Task<ReleasePlanDto?> ImportYamlAsync(string yaml, bool force = false)
    {
        var r = await http.PostAsJsonAsync("api/release-plans/import", new { Yaml = yaml, Force = force });
        if (!r.IsSuccessStatusCode) return null;
        return await r.Content.ReadFromJsonAsync<ReleasePlanDto>();
    }

    public async Task<ReleasePlanDto?> ReorderStagesAsync(Guid planId, List<Guid> stageIds)
    {
        var r = await http.PatchAsJsonAsync($"api/release-plans/{planId}/stages/reorder", new { StageIds = stageIds });
        if (!r.IsSuccessStatusCode) return null;
        return await r.Content.ReadFromJsonAsync<ReleasePlanDto>();
    }

    public async Task<ReleasePlanDto?> MoveItemAsync(Guid planId, Guid itemId, Guid targetStageId)
    {
        var r = await http.PostAsJsonAsync($"api/release-plans/{planId}/items/{itemId}/move", new { TargetStageId = targetStageId });
        if (!r.IsSuccessStatusCode) return null;
        return await r.Content.ReadFromJsonAsync<ReleasePlanDto>();
    }

    public async Task<ReleasePlanDto?> AddItemAsync(Guid planId, Guid stageId, Guid mrId)
    {
        var r = await http.PostAsJsonAsync($"api/release-plans/{planId}/stages/{stageId}/items", new { MergeRequestId = mrId });
        if (!r.IsSuccessStatusCode) return null;
        return await r.Content.ReadFromJsonAsync<ReleasePlanDto>();
    }

    public async Task<ReleasePlanDto?> RemoveItemAsync(Guid planId, Guid itemId)
    {
        var r = await http.DeleteAsync($"api/release-plans/{planId}/items/{itemId}");
        if (!r.IsSuccessStatusCode) return null;
        return await r.Content.ReadFromJsonAsync<ReleasePlanDto>();
    }

    // Merge Requests
    public Task<PagedResult<MrDto>?> GetMergeRequestsAsync(string? status = null, int page = 1, int pageSize = 50) =>
        http.GetFromJsonAsync<PagedResult<MrDto>>($"api/merge-requests?status={status ?? ""}&page={page}&pageSize={pageSize}");

    // VCS Connections
    public Task<PagedResult<VcsConnectionDto>?> GetVcsConnectionsAsync(int page = 1) =>
        http.GetFromJsonAsync<PagedResult<VcsConnectionDto>>($"api/vcs-connections?page={page}&pageSize=50");

    public async Task<bool> CreateVcsConnectionAsync(string name, string vcsType, string apiUrl, string accessToken)
    {
        var r = await http.PostAsJsonAsync("api/vcs-connections", new { Name = name, VcsType = vcsType, ApiUrl = apiUrl, AccessToken = accessToken });
        return r.IsSuccessStatusCode;
    }

    public async Task<bool> UpdateVcsConnectionAsync(Guid id, string name, string vcsType, string apiUrl, string accessToken)
    {
        var r = await http.PutAsJsonAsync($"api/vcs-connections/{id}", new { Name = name, VcsType = vcsType, ApiUrl = apiUrl, AccessToken = accessToken });
        return r.IsSuccessStatusCode;
    }

    public async Task<bool> DeleteVcsConnectionAsync(Guid id)
    {
        var r = await http.DeleteAsync($"api/vcs-connections/{id}");
        return r.IsSuccessStatusCode;
    }

    // Tracker Connections
    public Task<PagedResult<TrackerConnectionDto>?> GetTrackerConnectionsAsync(int page = 1) =>
        http.GetFromJsonAsync<PagedResult<TrackerConnectionDto>>($"api/tracker-connections?page={page}&pageSize=50");

    public async Task<bool> CreateTrackerConnectionAsync(string name, string trackerType, string apiUrl, string? orgId, string accessToken)
    {
        var r = await http.PostAsJsonAsync("api/tracker-connections", new { Name = name, TrackerType = trackerType, ApiUrl = apiUrl, OrgId = orgId, AccessToken = accessToken });
        return r.IsSuccessStatusCode;
    }

    public async Task<bool> UpdateTrackerConnectionAsync(Guid id, string name, string trackerType, string apiUrl, string? orgId, string accessToken)
    {
        var r = await http.PutAsJsonAsync($"api/tracker-connections/{id}", new { Name = name, TrackerType = trackerType, ApiUrl = apiUrl, OrgId = orgId, AccessToken = accessToken });
        return r.IsSuccessStatusCode;
    }

    public async Task<bool> DeleteTrackerConnectionAsync(Guid id)
    {
        var r = await http.DeleteAsync($"api/tracker-connections/{id}");
        return r.IsSuccessStatusCode;
    }

    // Repositories
    public Task<PagedResult<RepositoryDto>?> GetRepositoriesAsync(int page = 1) =>
        http.GetFromJsonAsync<PagedResult<RepositoryDto>>($"api/repositories?page={page}&pageSize=50");

    public async Task<bool> CreateRepositoryAsync(string name, string externalId, Guid connectionId)
    {
        var r = await http.PostAsJsonAsync("api/repositories", new { Name = name, ExternalId = externalId, ConnectionId = connectionId });
        return r.IsSuccessStatusCode;
    }

    public async Task<bool> DeleteRepositoryAsync(Guid id)
    {
        var r = await http.DeleteAsync($"api/repositories/{id}");
        return r.IsSuccessStatusCode;
    }

    // Stacks
    public Task<PagedResult<StackDto>?> GetStacksAsync(int page = 1) =>
        http.GetFromJsonAsync<PagedResult<StackDto>>($"api/stacks?page={page}&pageSize=50");

    public async Task<bool> CreateStackAsync(string name)
    {
        var r = await http.PostAsJsonAsync("api/stacks", new { Name = name });
        return r.IsSuccessStatusCode;
    }

    public async Task<bool> AddStackDependencyAsync(Guid fromStackId, Guid toStackId, string type)
    {
        var r = await http.PostAsJsonAsync($"api/stacks/{fromStackId}/dependencies", new { ToStackId = toStackId, Type = type });
        return r.IsSuccessStatusCode;
    }

    public async Task<bool> AssignRepositoryToStackAsync(Guid stackId, Guid repositoryId)
    {
        var r = await http.PostAsJsonAsync($"api/stacks/{stackId}/repositories", new { RepositoryId = repositoryId });
        return r.IsSuccessStatusCode;
    }

    // Permissions
    public Task<List<PermissionClaimDto>?> GetPermissionClaimsAsync() =>
        http.GetFromJsonAsync<List<PermissionClaimDto>>("api/permissions/claims");

    public Task<List<GroupMappingDto>?> GetGroupMappingsAsync() =>
        http.GetFromJsonAsync<List<GroupMappingDto>>("api/permissions/group-mappings");

    public async Task<bool> AddGroupMappingAsync(string adGroupSid, Guid permissionClaimId)
    {
        var r = await http.PostAsJsonAsync("api/permissions/group-mappings", new { AdGroupSid = adGroupSid, PermissionClaimId = permissionClaimId });
        return r.IsSuccessStatusCode;
    }

    public async Task<bool> RemoveGroupMappingAsync(Guid id)
    {
        var r = await http.DeleteAsync($"api/permissions/group-mappings/{id}");
        return r.IsSuccessStatusCode;
    }

    public async Task<bool> AddUserOverrideAsync(string userId, Guid permissionClaimId)
    {
        var r = await http.PostAsJsonAsync("api/permissions/user-overrides", new { UserId = userId, PermissionClaimId = permissionClaimId });
        return r.IsSuccessStatusCode;
    }
}
