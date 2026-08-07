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

    /// <summary>
    /// The task's active plan as a YAML document.
    /// </summary>
    /// <remarks>
    /// Read as text, not JSON: the endpoint answers <c>text/yaml</c> because the document's whole
    /// point is being readable, and JSON-escaping it would defeat that.
    /// </remarks>
    public async Task<string> ExportTaskPlanYamlAsync(Guid taskId, CancellationToken ct = default)
    {
        var response = await http.GetAsync($"api/tasks/{taskId}/plan/export", ct);
        if (!response.IsSuccessStatusCode)
            throw new ApiException(await ReadErrorAsync(response, ct), response.StatusCode);

        return await response.Content.ReadAsStringAsync(ct);
    }

    // ---- ordering rules (mirrors PlanningController) ---------------------------

    /// <summary>The ordering-rule document as stored.</summary>
    public Task<OrderingRulesDocumentDto> GetOrderingRulesAsync(CancellationToken ct = default) =>
        GetAsync<OrderingRulesDocumentDto>("api/planning/rules", ct);

    /// <summary>Checks a document without saving, reporting problems and what each group selects.</summary>
    public Task<OrderingRulesValidationDto> ValidateOrderingRulesAsync(string document, CancellationToken ct = default) =>
        SendAsync<OrderingRulesValidationDto>(
            () => http.PostAsJsonAsync("api/planning/rules/validate", new { Document = document }, ct), ct);

    /// <summary>Saves the document. An invalid one is refused with the problems listed.</summary>
    public Task SaveOrderingRulesAsync(string document, CancellationToken ct = default) =>
        SendAsync(() => http.PutAsJsonAsync("api/planning/rules", new { Document = document }, ct), ct);

    /// <summary>The rules configured on screen, written out as a document ready to adopt.</summary>
    public Task<OrderingRulesDocumentDto> OrderingRulesFromScreenAsync(CancellationToken ct = default) =>
        GetAsync<OrderingRulesDocumentDto>("api/planning/rules/from-repository-ordering", ct);

    /// <summary>
    /// Deployable work by task and repository — merge requests and the branches nothing has raised yet.
    /// </summary>
    /// <param name="environmentId">Environment to judge readiness against, or null for none.</param>
    /// <param name="state">Optional state filter.</param>
    /// <param name="search">Free text over task key, repository and branch.</param>
    /// <param name="page">1-based page.</param>
    /// <param name="pageSize">Rows per page.</param>
    /// <param name="ct">Cancellation token.</param>
    public Task<WorkItemsResult> GetWorkItemsAsync(
        Guid? environmentId = null, string? state = null, string? search = null,
        int page = 1, int pageSize = 50, CancellationToken ct = default) =>
        GetAsync<WorkItemsResult>(
            $"api/work-items?environmentId={environmentId}&state={Uri.EscapeDataString(state ?? "")}"
            + $"&search={Uri.EscapeDataString(search ?? "")}&page={page}&pageSize={pageSize}", ct);

    /// <summary>
    /// The labels merge requests actually carry, so a readiness rule can be built from real values
    /// rather than a remembered spelling.
    /// </summary>
    public Task<List<string>> GetMergeRequestLabelsAsync(CancellationToken ct = default) =>
        GetAsync<List<string>>("api/merge-requests/labels", ct);

    /// <summary>Pins a status by hand — one of the two ways an MR is marked deployable.</summary>
    public Task SetMergeRequestStatusAsync(Guid id, string status, CancellationToken ct = default) =>
        SendAsync(() => http.PatchAsJsonAsync($"api/merge-requests/{id}/status", new { Status = status }, ct), ct);

    // ---- providers ------------------------------------------------------------

    /// <summary>
    /// The VCS providers this build registers, and the settings each declares.
    /// </summary>
    /// <remarks>
    /// The connection form is built from this rather than from a list of provider names compiled
    /// into the PWA. That list was previously a literal <c>["GitLab"]</c> next to a fixed set of
    /// fields, so a second provider meant editing the page — and a provider needing its own field
    /// had nowhere to put it.
    /// </remarks>
    public Task<List<ProviderTypeDto>> GetVcsProviderTypesAsync(CancellationToken ct = default) =>
        GetAsync<List<ProviderTypeDto>>("api/providers/vcs", ct);

    /// <summary>The tracker providers this build registers, and the settings each declares.</summary>
    public Task<List<ProviderTypeDto>> GetTrackerProviderTypesAsync(CancellationToken ct = default) =>
        GetAsync<List<ProviderTypeDto>>("api/providers/trackers", ct);

    // ---- VCS connections ------------------------------------------------------

    public Task<PagedResult<VcsConnectionDto>> GetVcsConnectionsAsync(int page = 1, CancellationToken ct = default) =>
        GetAsync<PagedResult<VcsConnectionDto>>($"api/vcs-connections?page={page}&pageSize=50", ct);

    /// <param name="settings">Provider-specific settings, keyed as that provider's schema declares them.</param>
    public Task CreateVcsConnectionAsync(
        string name, string vcsType, string apiUrl, string accessToken,
        Dictionary<string, string?>? settings = null,
        CancellationToken ct = default) =>
        SendAsync(() => http.PostAsJsonAsync("api/vcs-connections",
            new
            {
                Name = name, VcsType = vcsType, ApiUrl = apiUrl, AccessToken = accessToken,
                Settings = settings
            }, ct), ct);

    /// <param name="accessToken">Blank keeps the stored token.</param>
    /// <param name="settings">
    /// Provider-specific settings. An omitted secret keeps the stored one — the same convention as
    /// the token, and why this sends only the keys the operator actually filled in.
    /// </param>
    public Task UpdateVcsConnectionAsync(
        Guid id, string name, string apiUrl, string? accessToken,
        Dictionary<string, string?>? settings = null,
        CancellationToken ct = default) =>
        SendAsync(() => http.PutAsJsonAsync($"api/vcs-connections/{id}",
            new
            {
                Name = name, ApiUrl = apiUrl, AccessToken = accessToken, Settings = settings
            }, ct), ct);

    public Task DeleteVcsConnectionAsync(Guid id, CancellationToken ct = default) =>
        SendAsync(() => http.DeleteAsync($"api/vcs-connections/{id}", ct), ct);

    /// <summary>Polls a poll-mode connection's open merge requests now, returning how many were emitted.</summary>
    public Task<PollResultDto> PollVcsConnectionAsync(Guid id, CancellationToken ct = default) =>
        SendAsync<PollResultDto>(() => http.PostAsync($"api/vcs-connections/{id}/poll", content: null, ct), ct);

    /// <summary>The connectors, deploy strategies and action handlers this build has installed.</summary>
    public Task<List<PluginDto>> GetPluginsAsync(CancellationToken ct = default) =>
        GetAsync<List<PluginDto>>("api/plugins", ct);

    // ---- tracker connections --------------------------------------------------

    public Task<PagedResult<TrackerConnectionDto>> GetTrackerConnectionsAsync(int page = 1, CancellationToken ct = default) =>
        GetAsync<PagedResult<TrackerConnectionDto>>($"api/tracker-connections?page={page}&pageSize=50", ct);

    /// <param name="settings">Provider-specific settings, keyed as that provider's schema declares them.</param>
    public Task CreateTrackerConnectionAsync(
        string name, string trackerType, string apiUrl, string accessToken,
        Dictionary<string, string?>? settings = null, CancellationToken ct = default) =>
        SendAsync(() => http.PostAsJsonAsync("api/tracker-connections",
            new
            {
                Name = name, TrackerType = trackerType, ApiUrl = apiUrl,
                AccessToken = accessToken, Settings = settings
            }, ct), ct);

    /// <param name="accessToken">Blank keeps the stored token.</param>
    /// <param name="settings">An omitted secret keeps the stored one, as with the token.</param>
    public Task UpdateTrackerConnectionAsync(
        Guid id, string name, string apiUrl, string? accessToken,
        Dictionary<string, string?>? settings = null, CancellationToken ct = default) =>
        SendAsync(() => http.PutAsJsonAsync($"api/tracker-connections/{id}",
            new { Name = name, ApiUrl = apiUrl, AccessToken = accessToken, Settings = settings }, ct), ct);

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

    /// <param name="readinessRuleId">The environment's default readiness rule, or null for no gate (needs approval).</param>
    public Task CreateEnvironmentAsync(
        string key, string name, int order, bool isEnabled,
        Guid? readinessRuleId = null, CancellationToken ct = default) =>
        SendAsync(() => http.PostAsJsonAsync("api/environments",
            new { Key = key, Name = name, Order = order, IsEnabled = isEnabled, ReadinessRuleId = readinessRuleId }, ct), ct);

    /// <param name="readinessRuleId">The default readiness rule, or null for no gate; removing a gate needs approval.</param>
    public Task UpdateEnvironmentAsync(
        Guid id, string name, int order, bool isEnabled,
        Guid? readinessRuleId = null, CancellationToken ct = default) =>
        SendAsync(() => http.PutAsJsonAsync($"api/environments/{id}",
            new { Key = "", Name = name, Order = order, IsEnabled = isEnabled, ReadinessRuleId = readinessRuleId }, ct), ct);

    public Task DeleteEnvironmentAsync(Guid id, CancellationToken ct = default) =>
        SendAsync(() => http.DeleteAsync($"api/environments/{id}", ct), ct);

    // ---- readiness rules ------------------------------------------------------

    public Task<List<ReadinessRuleDto>> GetReadinessRulesAsync(CancellationToken ct = default) =>
        GetAsync<List<ReadinessRuleDto>>("api/readiness-rules", ct);

    /// <param name="mode">"AnyOf" or "AllOf".</param>
    /// <param name="requiredSignals">The signal tokens a merge request must carry, e.g. label:ready-for-prod.</param>
    public Task CreateReadinessRuleAsync(
        string name, string mode, IReadOnlyList<string> requiredSignals, CancellationToken ct = default) =>
        SendAsync(() => http.PostAsJsonAsync("api/readiness-rules",
            new { Name = name, Mode = mode, RequiredSignals = requiredSignals }, ct), ct);

    public Task UpdateReadinessRuleAsync(
        Guid id, string name, string mode, IReadOnlyList<string> requiredSignals, CancellationToken ct = default) =>
        SendAsync(() => http.PutAsJsonAsync($"api/readiness-rules/{id}",
            new { Name = name, Mode = mode, RequiredSignals = requiredSignals }, ct), ct);

    public Task DeleteReadinessRuleAsync(Guid id, CancellationToken ct = default) =>
        SendAsync(() => http.DeleteAsync($"api/readiness-rules/{id}", ct), ct);

    // ---- deploy targets -------------------------------------------------------

    /// <summary>The deploy strategies this build registers, and the settings each declares.</summary>
    public Task<List<ProviderTypeDto>> GetDeployStrategiesAsync(CancellationToken ct = default) =>
        GetAsync<List<ProviderTypeDto>>("api/providers/deploy-strategies", ct);

    public Task<List<DeployTargetDto>> GetDeployTargetsAsync(CancellationToken ct = default) =>
        GetAsync<List<DeployTargetDto>>("api/deploy-targets", ct);

    /// <param name="settings">Strategy settings, keyed as the strategy declares them.</param>
    /// <param name="readinessRuleId">Readiness-rule override for this pair, or null to use the environment default.</param>
    public Task CreateDeployTargetAsync(
        Guid repositoryId, Guid environmentId, string deployStrategyKey, string redeployPolicy,
        Dictionary<string, string?>? settings = null, Guid? readinessRuleId = null, CancellationToken ct = default) =>
        SendAsync(() => http.PostAsJsonAsync("api/deploy-targets",
            new
            {
                RepositoryId = repositoryId, EnvironmentId = environmentId,
                DeployStrategyKey = deployStrategyKey, RedeployPolicy = redeployPolicy,
                Settings = settings, ReadinessRuleId = readinessRuleId
            }, ct), ct);

    /// <param name="settings">An omitted secret keeps the stored one, as with a connection token.</param>
    /// <param name="readinessRuleId">Readiness-rule override for this pair, or null to use the environment default.</param>
    public Task UpdateDeployTargetAsync(
        Guid id, Guid repositoryId, Guid environmentId, string deployStrategyKey, string redeployPolicy,
        Dictionary<string, string?>? settings = null, Guid? readinessRuleId = null, CancellationToken ct = default) =>
        SendAsync(() => http.PutAsJsonAsync($"api/deploy-targets/{id}",
            new
            {
                RepositoryId = repositoryId, EnvironmentId = environmentId,
                DeployStrategyKey = deployStrategyKey, RedeployPolicy = redeployPolicy,
                Settings = settings, ReadinessRuleId = readinessRuleId
            }, ct), ct);

    public Task DeleteDeployTargetAsync(Guid id, CancellationToken ct = default) =>
        SendAsync(() => http.DeleteAsync($"api/deploy-targets/{id}", ct), ct);

    // ---- readiness pins -------------------------------------------------------

    public Task<List<ReadinessPinDto>> GetReadinessPinsAsync(Guid mergeRequestId, CancellationToken ct = default) =>
        GetAsync<List<ReadinessPinDto>>($"api/readiness-pins?mergeRequestId={mergeRequestId}", ct);

    /// <param name="isReady">True admits the merge request into the environment; false holds it out.</param>
    public Task SetReadinessPinAsync(
        Guid mergeRequestId, Guid environmentId, bool isReady, string? reason = null, CancellationToken ct = default) =>
        SendAsync(() => http.PutAsJsonAsync("api/readiness-pins",
            new { MergeRequestId = mergeRequestId, EnvironmentId = environmentId, IsReady = isReady, Reason = reason }, ct), ct);

    public Task DeleteReadinessPinAsync(Guid id, CancellationToken ct = default) =>
        SendAsync(() => http.DeleteAsync($"api/readiness-pins/{id}", ct), ct);

    /// <summary>The merge requests forced into or out of a task's rollout.</summary>
    /// <remarks>
    /// Read separately from the plan, because an excluded merge request is by definition absent from
    /// it — this is the only way back for a decision that would otherwise be one-way.
    /// </remarks>
    public Task<List<PlanMembershipDto>> GetPlanMembershipAsync(Guid taskId, CancellationToken ct = default) =>
        GetAsync<List<PlanMembershipDto>>($"api/planning/tasks/{taskId}/membership", ct);

    /// <param name="state">"auto", "included" or "excluded".</param>
    public Task SetPlanMembershipAsync(
        Guid taskId, Guid mergeRequestId, string state, CancellationToken ct = default) =>
        SendAsync(() => http.PutAsJsonAsync(
            $"api/planning/tasks/{taskId}/membership/{mergeRequestId}", new { State = state }, ct), ct);

    // ---- rollouts -------------------------------------------------------------

    /// <param name="redeploy">Redeploy already-deployed merge requests, where their target permits it.</param>
    public Task<RolloutDto> LaunchRolloutAsync(Guid taskId, Guid environmentId, bool redeploy = false, CancellationToken ct = default) =>
        SendAsync<RolloutDto>(
            () => http.PostAsJsonAsync($"api/tasks/{taskId}/rollouts", new { EnvironmentId = environmentId, Redeploy = redeploy }, ct), ct);

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
