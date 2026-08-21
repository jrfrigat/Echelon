using System.Net.Http.Json;
using Echelon.Application.DTOs;

namespace Echelon.Pwa.Services.Api;

/// <summary>Tasks, their timelines and their rollout plans.</summary>
/// <param name="http">The client this area talks over.</param>
public sealed class TasksApi(HttpClient http) : ApiClient(http)
{
    /// <summary>
    /// The task's active plan as a YAML document.
    /// </summary>
    /// <remarks>
    /// Read as text, not JSON: the endpoint answers <c>text/yaml</c> because the document's whole
    /// point is being readable, and JSON-escaping it would defeat that.
    /// </remarks>
    public async Task<string> ExportTaskPlanYamlAsync(Guid taskId, CancellationToken ct = default)
    {
        var response = await Http.GetAsync($"api/tasks/{taskId}/plan/export", ct);
        if (!response.IsSuccessStatusCode)
            throw new ApiException(await ReadErrorAsync(response, ct), response.StatusCode);

        return await response.Content.ReadAsStringAsync(ct);
    }

    /// <summary>A page of tasks, narrowed by the grid's column filter boxes.</summary>
    /// <remarks>The filters go to the server because the page here is one slice of the list; see the API.</remarks>
    public Task<PagedResult<TaskListItemDto>> GetTasksAsync(
        int page = 1, int pageSize = 50,
        string? key = null, string? title = null, string? status = null,
        CancellationToken ct = default) =>
        GetAsync<PagedResult<TaskListItemDto>>(
            $"api/tasks?{Query(("page", page), ("pageSize", pageSize), ("key", key), ("title", title), ("status", status))}", ct);

    /// <summary>The task itself - its parent and subtasks - which exists whether or not a plan does.</summary>
    public Task<TaskDetailDto?> GetTaskAsync(Guid taskId, CancellationToken ct = default) =>
        GetOrNullAsync<TaskDetailDto>($"api/tasks/{taskId}", ct);

    /// <summary>Everything that has happened to the task, newest first.</summary>
    public Task<TaskTimelineDto?> GetTaskTimelineAsync(Guid taskId, int limit = 200, CancellationToken ct = default) =>
        GetOrNullAsync<TaskTimelineDto>($"api/tasks/{taskId}/timeline?limit={limit}", ct);

    public Task<RolloutPlanDto?> GetTaskPlanAsync(Guid taskId, CancellationToken ct = default) =>
        GetOrNullAsync<RolloutPlanDto>($"api/tasks/{taskId}/plan", ct);

    public Task<RolloutPlanDto> RecalculateTaskPlanAsync(Guid taskId, CancellationToken ct = default) =>
        SendAsync<RolloutPlanDto>(() => Http.PostAsync($"api/tasks/{taskId}/plan/recalculate", null, ct), ct);

    /// <summary>The merge requests forced into or out of a task's rollout.</summary>
    /// <remarks>
    /// Read separately from the plan, because an excluded merge request is by definition absent from
    /// it - this is the only way back for a decision that would otherwise be one-way.
    /// </remarks>
    public Task<List<PlanMembershipDto>> GetPlanMembershipAsync(Guid taskId, CancellationToken ct = default) =>
        GetAsync<List<PlanMembershipDto>>($"api/planning/tasks/{taskId}/membership", ct);

    /// <param name="state">"auto", "included" or "excluded".</param>
    public Task SetPlanMembershipAsync(
        Guid taskId, Guid mergeRequestId, string state, CancellationToken ct = default) =>
        SendAsync(() => Http.PutAsJsonAsync(
            $"api/planning/tasks/{taskId}/membership/{mergeRequestId}", new { State = state }, Json, ct), ct);
}
