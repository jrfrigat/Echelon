using System.Net;
using System.Net.Http.Json;
using ReleaseOrchestrator.Pwa.Models;

namespace ReleaseOrchestrator.Pwa.Services;

/// <summary>Carries the server's own explanation of a failure so the UI can show it.</summary>
public class ApiException(string message, HttpStatusCode statusCode) : Exception(message)
{
    public HttpStatusCode StatusCode { get; } = statusCode;
}

/// <summary>
/// Typed API client.
///
/// Failures surface as <see cref="ApiException"/> carrying the server's message. These
/// methods used to return null or false on error, discarding the reason: the API answers
/// with ProblemDetails or an { error } body saying exactly what was wrong, and the user was
/// shown a generic "failed to save" instead.
/// </summary>
public class ApiService(HttpClient http)
{
    // ---- release plans --------------------------------------------------------

    public Task<ReleasePlanDto?> GetActivePlanAsync(CancellationToken ct = default) =>
        GetOrNullAsync<ReleasePlanDto>("api/release-plans/active", ct);

    public Task<ReleasePlanDto?> GetPlanByIdAsync(Guid id, CancellationToken ct = default) =>
        GetOrNullAsync<ReleasePlanDto>($"api/release-plans/{id}", ct);

    public Task<ReleasePlanDto> RecalculatePlanAsync(CancellationToken ct = default) =>
        SendAsync<ReleasePlanDto>(() => http.PostAsync("api/release-plans/recalculate", null, ct), ct);

    public async Task<string> ExportYamlAsync(Guid planId, CancellationToken ct = default)
    {
        var response = await http.GetAsync($"api/release-plans/{planId}/export", ct);
        await EnsureSuccessAsync(response, ct);
        return await response.Content.ReadAsStringAsync(ct);
    }

    public Task<ReleasePlanDto> ImportYamlAsync(string yaml, bool force = false, CancellationToken ct = default) =>
        SendAsync<ReleasePlanDto>(
            () => http.PostAsJsonAsync("api/release-plans/import", new { Yaml = yaml, Force = force }, ct), ct);

    public Task<ReleasePlanDto> ReorderStagesAsync(Guid planId, List<Guid> stageIds, CancellationToken ct = default) =>
        SendAsync<ReleasePlanDto>(
            () => http.PatchAsJsonAsync($"api/release-plans/{planId}/stages/reorder", new { StageIds = stageIds }, ct), ct);

    public Task<ReleasePlanDto> MoveItemAsync(Guid planId, Guid itemId, Guid targetStageId, CancellationToken ct = default) =>
        SendAsync<ReleasePlanDto>(
            () => http.PostAsJsonAsync($"api/release-plans/{planId}/items/{itemId}/move", new { TargetStageId = targetStageId }, ct), ct);

    public Task<ReleasePlanDto> AddItemAsync(Guid planId, Guid stageId, Guid mrId, CancellationToken ct = default) =>
        SendAsync<ReleasePlanDto>(
            () => http.PostAsJsonAsync($"api/release-plans/{planId}/stages/{stageId}/items", new { MergeRequestId = mrId }, ct), ct);

    public Task<ReleasePlanDto> RemoveItemAsync(Guid planId, Guid itemId, CancellationToken ct = default) =>
        SendAsync<ReleasePlanDto>(() => http.DeleteAsync($"api/release-plans/{planId}/items/{itemId}", ct), ct);

    // ---- merge requests -------------------------------------------------------

    public Task<PagedResult<MrDto>> GetMergeRequestsAsync(
        string? status = null, int page = 1, int pageSize = 50, CancellationToken ct = default) =>
        GetAsync<PagedResult<MrDto>>(
            $"api/merge-requests?status={Uri.EscapeDataString(status ?? "")}&page={page}&pageSize={pageSize}", ct);

    /// <summary>Pins a status by hand — one of the two ways an MR enters the plan.</summary>
    public Task SetMergeRequestStatusAsync(Guid id, string status, CancellationToken ct = default) =>
        SendAsync(() => http.PatchAsJsonAsync($"api/merge-requests/{id}/status", new { Status = status }, ct), ct);

    // ---- VCS connections ------------------------------------------------------

    public Task<PagedResult<VcsConnectionDto>> GetVcsConnectionsAsync(int page = 1, CancellationToken ct = default) =>
        GetAsync<PagedResult<VcsConnectionDto>>($"api/vcs-connections?page={page}&pageSize=50", ct);

    public Task CreateVcsConnectionAsync(
        string name, string vcsType, string apiUrl, string accessToken, string? readyForDeployLabel,
        CancellationToken ct = default) =>
        SendAsync(() => http.PostAsJsonAsync("api/vcs-connections",
            new { Name = name, VcsType = vcsType, ApiUrl = apiUrl, AccessToken = accessToken, ReadyForDeployLabel = readyForDeployLabel }, ct), ct);

    /// <param name="accessToken">Blank keeps the stored token.</param>
    public Task UpdateVcsConnectionAsync(
        Guid id, string name, string apiUrl, string? accessToken, string? readyForDeployLabel,
        CancellationToken ct = default) =>
        SendAsync(() => http.PutAsJsonAsync($"api/vcs-connections/{id}",
            new { Name = name, ApiUrl = apiUrl, AccessToken = accessToken, ReadyForDeployLabel = readyForDeployLabel }, ct), ct);

    public Task DeleteVcsConnectionAsync(Guid id, CancellationToken ct = default) =>
        SendAsync(() => http.DeleteAsync($"api/vcs-connections/{id}", ct), ct);

    // ---- tracker connections --------------------------------------------------

    public Task<PagedResult<TrackerConnectionDto>> GetTrackerConnectionsAsync(int page = 1, CancellationToken ct = default) =>
        GetAsync<PagedResult<TrackerConnectionDto>>($"api/tracker-connections?page={page}&pageSize=50", ct);

    public Task CreateTrackerConnectionAsync(
        string name, string trackerType, string apiUrl, string? orgId, string accessToken,
        CancellationToken ct = default) =>
        SendAsync(() => http.PostAsJsonAsync("api/tracker-connections",
            new { Name = name, TrackerType = trackerType, ApiUrl = apiUrl, OrgId = orgId, AccessToken = accessToken }, ct), ct);

    /// <param name="accessToken">Blank keeps the stored token.</param>
    public Task UpdateTrackerConnectionAsync(
        Guid id, string name, string apiUrl, string? orgId, string? accessToken,
        CancellationToken ct = default) =>
        SendAsync(() => http.PutAsJsonAsync($"api/tracker-connections/{id}",
            new { Name = name, ApiUrl = apiUrl, OrgId = orgId, AccessToken = accessToken }, ct), ct);

    public Task DeleteTrackerConnectionAsync(Guid id, CancellationToken ct = default) =>
        SendAsync(() => http.DeleteAsync($"api/tracker-connections/{id}", ct), ct);

    // ---- repositories ---------------------------------------------------------

    public Task<PagedResult<RepositoryDto>> GetRepositoriesAsync(int page = 1, CancellationToken ct = default) =>
        GetAsync<PagedResult<RepositoryDto>>($"api/repositories?page={page}&pageSize=50", ct);

    public Task CreateRepositoryAsync(string name, string externalId, Guid connectionId, CancellationToken ct = default) =>
        SendAsync(() => http.PostAsJsonAsync("api/repositories",
            new { Name = name, ExternalId = externalId, ConnectionId = connectionId }, ct), ct);

    public Task DeleteRepositoryAsync(Guid id, CancellationToken ct = default) =>
        SendAsync(() => http.DeleteAsync($"api/repositories/{id}", ct), ct);

    // ---- stacks ---------------------------------------------------------------

    public Task<PagedResult<StackDto>> GetStacksAsync(int page = 1, CancellationToken ct = default) =>
        GetAsync<PagedResult<StackDto>>($"api/stacks?page={page}&pageSize=50", ct);

    public Task CreateStackAsync(string name, CancellationToken ct = default) =>
        SendAsync(() => http.PostAsJsonAsync("api/stacks", new { Name = name }, ct), ct);

    public Task AddStackDependencyAsync(Guid fromStackId, Guid toStackId, string type, CancellationToken ct = default) =>
        SendAsync(() => http.PostAsJsonAsync($"api/stacks/{fromStackId}/dependencies",
            new { ToStackId = toStackId, Type = type }, ct), ct);

    public Task AssignRepositoryToStackAsync(Guid stackId, Guid repositoryId, CancellationToken ct = default) =>
        SendAsync(() => http.PostAsJsonAsync($"api/stacks/{stackId}/repositories",
            new { RepositoryId = repositoryId }, ct), ct);

    // ---- permissions ----------------------------------------------------------

    public Task<List<PermissionClaimDto>> GetPermissionClaimsAsync(CancellationToken ct = default) =>
        GetAsync<List<PermissionClaimDto>>("api/permissions/claims", ct);

    public Task<List<GroupMappingDto>> GetGroupMappingsAsync(CancellationToken ct = default) =>
        GetAsync<List<GroupMappingDto>>("api/permissions/group-mappings", ct);

    public Task AddGroupMappingAsync(string adGroupSid, Guid permissionClaimId, CancellationToken ct = default) =>
        SendAsync(() => http.PostAsJsonAsync("api/permissions/group-mappings",
            new { AdGroupSid = adGroupSid, PermissionClaimId = permissionClaimId }, ct), ct);

    public Task RemoveGroupMappingAsync(Guid id, CancellationToken ct = default) =>
        SendAsync(() => http.DeleteAsync($"api/permissions/group-mappings/{id}", ct), ct);

    public Task AddUserOverrideAsync(string userId, Guid permissionClaimId, CancellationToken ct = default) =>
        SendAsync(() => http.PostAsJsonAsync("api/permissions/user-overrides",
            new { UserId = userId, PermissionClaimId = permissionClaimId }, ct), ct);

    // ---- plumbing -------------------------------------------------------------

    private async Task<T> GetAsync<T>(string url, CancellationToken ct)
    {
        var response = await http.GetAsync(url, ct);
        await EnsureSuccessAsync(response, ct);

        return await response.Content.ReadFromJsonAsync<T>(ct)
               ?? throw new ApiException("The server returned an empty response.", response.StatusCode);
    }

    /// <summary>For endpoints where absence is a normal answer rather than a failure.</summary>
    private async Task<T?> GetOrNullAsync<T>(string url, CancellationToken ct)
    {
        var response = await http.GetAsync(url, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return default;

        await EnsureSuccessAsync(response, ct);
        return await response.Content.ReadFromJsonAsync<T>(ct);
    }

    private async Task<T> SendAsync<T>(Func<Task<HttpResponseMessage>> send, CancellationToken ct)
    {
        var response = await send();
        await EnsureSuccessAsync(response, ct);

        return await response.Content.ReadFromJsonAsync<T>(ct)
               ?? throw new ApiException("The server returned an empty response.", response.StatusCode);
    }

    private async Task SendAsync(Func<Task<HttpResponseMessage>> send, CancellationToken ct)
        => await EnsureSuccessAsync(await send(), ct);

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;

        throw new ApiException(await ReadErrorAsync(response, ct), response.StatusCode);
    }

    /// <summary>
    /// The API reports failures two ways: ProblemDetails from the domain exception handler,
    /// and a plain { error } body from controller-level validation. Try both before falling
    /// back to the status code.
    /// </summary>
    private static async Task<string> ReadErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var body = await response.Content.ReadFromJsonAsync<ErrorBody>(ct);
            if (!string.IsNullOrWhiteSpace(body?.Detail)) return body.Detail;
            if (!string.IsNullOrWhiteSpace(body?.Error)) return body.Error;
            if (!string.IsNullOrWhiteSpace(body?.Title)) return body.Title;
        }
        catch
        {
            // Not JSON, or an unexpected shape — fall through to the generic message.
        }

        return response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => "Your session has expired. Please sign in again.",
            HttpStatusCode.Forbidden => "You do not have permission to do that.",
            HttpStatusCode.TooManyRequests => "Too many requests. Please wait a moment and retry.",
            _ => $"The request failed ({(int)response.StatusCode} {response.ReasonPhrase})."
        };
    }

    private record ErrorBody(string? Detail, string? Title, string? Error);
}
