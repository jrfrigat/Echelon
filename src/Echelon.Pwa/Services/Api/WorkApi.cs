using System.Net.Http.Json;
using Echelon.Application.DTOs;

namespace Echelon.Pwa.Services.Api;

/// <summary>Deployable work: merge requests, branches and their statuses.</summary>
/// <param name="http">The client this area talks over.</param>
public sealed class WorkApi(HttpClient http) : ApiClient(http)
{
    public Task<PagedResult<MrDto>> GetMergeRequestsAsync(
        string? status = null, int page = 1, int pageSize = 50, CancellationToken ct = default) =>
        GetAsync<PagedResult<MrDto>>(
            $"api/merge-requests?status={Uri.EscapeDataString(status ?? "")}&page={page}&pageSize={pageSize}", ct);

    /// <summary>
    /// Deployable work by task and repository - merge requests and the branches nothing has raised yet.
    /// </summary>
    /// <param name="environmentId">Environment to judge readiness against, or null for none.</param>
    /// <param name="state">Optional state filter.</param>
    /// <param name="search">Free text over task key, repository and branch.</param>
    /// <param name="page">1-based page.</param>
    /// <param name="pageSize">Rows per page.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <summary>A page of deployable work: the filter bar above the grid, plus its column filter boxes.</summary>
    public Task<WorkItemsResult> GetWorkItemsAsync(
        Guid? environmentId = null, string? state = null, string? search = null,
        string? taskKey = null, string? repository = null, string? connection = null, string? branch = null,
        int page = 1, int pageSize = 50, CancellationToken ct = default) =>
        GetAsync<WorkItemsResult>(
            $"api/work-items?{Query(
                ("environmentId", environmentId), ("state", state), ("search", search),
                ("taskKey", taskKey), ("repository", repository), ("connection", connection), ("branch", branch),
                ("page", page), ("pageSize", pageSize))}", ct);

    /// <summary>
    /// The labels merge requests actually carry, so a readiness rule can be built from real values
    /// rather than a remembered spelling.
    /// </summary>
    public Task<List<string>> GetMergeRequestLabelsAsync(CancellationToken ct = default) =>
        GetAsync<List<string>>("api/merge-requests/labels", ct);

    /// <summary>Pins a status by hand - one of the two ways an MR is marked deployable.</summary>
    public Task SetMergeRequestStatusAsync(Guid id, string status, CancellationToken ct = default) =>
        SendAsync(() => Http.PatchAsJsonAsync($"api/merge-requests/{id}/status", new { Status = status }, Json, ct), ct);
}
