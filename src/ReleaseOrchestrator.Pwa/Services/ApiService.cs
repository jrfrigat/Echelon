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
        string? ingestionMode = null, CancellationToken ct = default) =>
        SendAsync(() => http.PostAsJsonAsync("api/vcs-connections",
            new
            {
                Name = name, VcsType = vcsType, ApiUrl = apiUrl, AccessToken = accessToken,
                ReadyForDeployLabel = readyForDeployLabel, IngestionMode = ingestionMode
            }, ct), ct);

    /// <param name="accessToken">Blank keeps the stored token.</param>
    /// <param name="ingestionMode">Blank keeps the stored mode, for the same reason.</param>
    public Task UpdateVcsConnectionAsync(
        Guid id, string name, string apiUrl, string? accessToken, string? readyForDeployLabel,
        string? ingestionMode = null, CancellationToken ct = default) =>
        SendAsync(() => http.PutAsJsonAsync($"api/vcs-connections/{id}",
            new
            {
                Name = name, ApiUrl = apiUrl, AccessToken = accessToken,
                ReadyForDeployLabel = readyForDeployLabel, IngestionMode = ingestionMode
            }, ct), ct);

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

    // ---- default rollout plan (repository ordering) ----------------------------

    public Task<PagedResult<RepositoryOrderingDto>> GetRepositoryOrderingAsync(int page = 1, CancellationToken ct = default) =>
        GetAsync<PagedResult<RepositoryOrderingDto>>($"api/repository-ordering?page={page}&pageSize=50", ct);

    /// <summary>The order those rules add up to, derived server-side by the real ordering engine.</summary>
    public Task<DefaultPlanDto> GetDefaultPlanAsync(CancellationToken ct = default) =>
        GetAsync<DefaultPlanDto>("api/repository-ordering/plan", ct);

    /// <param name="fromRepositoryId">The repository that deploys later.</param>
    /// <param name="toRepositoryId">The repository that deploys first.</param>
    /// <param name="type">"Hard" (never dropped) or "Soft" (dropped first to break a cycle).</param>
    public Task CreateRepositoryOrderingAsync(
        Guid fromRepositoryId, Guid toRepositoryId, string type, CancellationToken ct = default) =>
        SendAsync(() => http.PostAsJsonAsync("api/repository-ordering",
            new { FromRepositoryId = fromRepositoryId, ToRepositoryId = toRepositoryId, Type = type }, ct), ct);

    public Task DeleteRepositoryOrderingAsync(Guid id, CancellationToken ct = default) =>
        SendAsync(() => http.DeleteAsync($"api/repository-ordering/{id}", ct), ct);

    // ---- tasks (per-task rollout plans) ---------------------------------------

    public Task<PagedResult<TaskListItemDto>> GetTasksAsync(int page = 1, CancellationToken ct = default) =>
        GetAsync<PagedResult<TaskListItemDto>>($"api/tasks?page={page}&pageSize=50", ct);

    /// <summary>The task itself — its parent and subtasks — which exists whether or not a plan does.</summary>
    public Task<TaskDetailDto?> GetTaskAsync(Guid taskId, CancellationToken ct = default) =>
        GetOrNullAsync<TaskDetailDto>($"api/tasks/{taskId}", ct);

    /// <summary>Everything that has happened to the task, newest first.</summary>
    public Task<TaskTimelineDto?> GetTaskTimelineAsync(Guid taskId, int limit = 200, CancellationToken ct = default) =>
        GetOrNullAsync<TaskTimelineDto>($"api/tasks/{taskId}/timeline?limit={limit}", ct);

    public Task<RolloutPlanDto?> GetTaskPlanAsync(Guid taskId, CancellationToken ct = default) =>
        GetOrNullAsync<RolloutPlanDto>($"api/tasks/{taskId}/plan", ct);

    public Task<RolloutPlanDto> RecalculateTaskPlanAsync(Guid taskId, CancellationToken ct = default) =>
        SendAsync<RolloutPlanDto>(() => http.PostAsync($"api/tasks/{taskId}/plan/recalculate", null, ct), ct);

    // ---- environments ---------------------------------------------------------

    public Task<List<EnvironmentDto>> GetEnvironmentsAsync(CancellationToken ct = default) =>
        GetAsync<List<EnvironmentDto>>("api/environments", ct);

    public Task CreateEnvironmentAsync(string key, string name, int order, bool isEnabled, CancellationToken ct = default) =>
        SendAsync(() => http.PostAsJsonAsync("api/environments",
            new { Key = key, Name = name, Order = order, IsEnabled = isEnabled }, ct), ct);

    public Task DeleteEnvironmentAsync(Guid id, CancellationToken ct = default) =>
        SendAsync(() => http.DeleteAsync($"api/environments/{id}", ct), ct);

    // ---- rollouts -------------------------------------------------------------

    public Task<RolloutDto> LaunchRolloutAsync(Guid taskId, Guid environmentId, CancellationToken ct = default) =>
        SendAsync<RolloutDto>(
            () => http.PostAsJsonAsync($"api/tasks/{taskId}/rollouts", new { EnvironmentId = environmentId }, ct), ct);

    public Task<RolloutDto?> GetRolloutAsync(Guid id, CancellationToken ct = default) =>
        GetOrNullAsync<RolloutDto>($"api/rollouts/{id}", ct);

    public Task<List<RolloutSummaryDto>> GetRolloutsAsync(Guid? taskId = null, CancellationToken ct = default) =>
        GetAsync<List<RolloutSummaryDto>>($"api/rollouts{(taskId is null ? "" : $"?taskId={taskId}")}", ct);

    public Task CancelRolloutAsync(Guid id, CancellationToken ct = default) =>
        SendAsync(() => http.PostAsync($"api/rollouts/{id}/cancel", null, ct), ct);

    public Task RetryRolloutStepAsync(Guid rolloutId, Guid stepId, CancellationToken ct = default) =>
        SendAsync(() => http.PostAsync($"api/rollouts/{rolloutId}/steps/{stepId}/retry", null, ct), ct);

    public Task SkipRolloutStepAsync(Guid rolloutId, Guid stepId, CancellationToken ct = default) =>
        SendAsync(() => http.PostAsync($"api/rollouts/{rolloutId}/steps/{stepId}/skip", null, ct), ct);

    // ---- action bindings ------------------------------------------------------

    public Task<List<ActionBindingDto>> GetActionBindingsAsync(CancellationToken ct = default) =>
        GetAsync<List<ActionBindingDto>>("api/action-bindings", ct);

    public Task<List<ActionTypeDto>> GetActionTypesAsync(CancellationToken ct = default) =>
        GetAsync<List<ActionTypeDto>>("api/action-bindings/types", ct);

    public Task CreateActionBindingAsync(
        string eventType, string actionType, string? scope, Dictionary<string, string> settings, int order, bool enabled,
        CancellationToken ct = default) =>
        SendAsync(() => http.PostAsJsonAsync("api/action-bindings",
            new { EventType = eventType, ActionType = actionType, Scope = scope, Settings = settings, Order = order, Enabled = enabled }, ct), ct);

    public Task DeleteActionBindingAsync(Guid id, CancellationToken ct = default) =>
        SendAsync(() => http.DeleteAsync($"api/action-bindings/{id}", ct), ct);

    // ---- request audit --------------------------------------------------------

    public Task<PagedResult<RequestAuditEntryDto>> GetRequestAuditAsync(
        int minutes, string? status, bool notableOnly, bool includeAuditTraffic, string? search,
        int page = 1, CancellationToken ct = default) =>
        GetAsync<PagedResult<RequestAuditEntryDto>>(
            $"api/request-audit?minutes={minutes}&status={Uri.EscapeDataString(status ?? "")}"
            + $"&notableOnly={notableOnly}&includeAuditTraffic={includeAuditTraffic}"
            + $"&search={Uri.EscapeDataString(search ?? "")}&page={page}&pageSize=50", ct);

    public Task<RequestAuditSummaryDto> GetRequestAuditSummaryAsync(int minutes, CancellationToken ct = default) =>
        GetAsync<RequestAuditSummaryDto>($"api/request-audit/summary?minutes={minutes}", ct);

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
