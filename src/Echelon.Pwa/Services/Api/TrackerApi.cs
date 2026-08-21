using System.Net.Http.Json;
using Echelon.Application.DTOs;

namespace Echelon.Pwa.Services.Api;

/// <summary>Tracker connections and the providers that back them.</summary>
/// <param name="http">The client this area talks over.</param>
public sealed class TrackerApi(HttpClient http) : ApiClient(http)
{
    /// <summary>The tracker providers this build registers, and the settings each declares.</summary>
    public Task<List<ProviderTypeDto>> GetTrackerProviderTypesAsync(CancellationToken ct = default) =>
        GetAsync<List<ProviderTypeDto>>("api/providers/trackers", ct);

    /// <summary>A page of tracker connections, narrowed by the grid's column filter boxes.</summary>
    public Task<PagedResult<TrackerConnectionDto>> GetTrackerConnectionsAsync(
        int page = 1, int pageSize = 50,
        string? name = null, string? type = null, string? apiUrl = null,
        CancellationToken ct = default) =>
        GetAsync<PagedResult<TrackerConnectionDto>>(
            $"api/tracker-connections?{Query(("page", page), ("pageSize", pageSize), ("name", name), ("type", type), ("apiUrl", apiUrl))}", ct);

    /// <param name="settings">Provider-specific settings, keyed as that provider's schema declares them.</param>
    public Task CreateTrackerConnectionAsync(
        string name, string trackerType, string apiUrl, string accessToken,
        Dictionary<string, string?>? settings = null, CancellationToken ct = default) =>
        SendAsync(() => Http.PostAsJsonAsync("api/tracker-connections",
            new
            {
                Name = name,
                TrackerType = trackerType,
                ApiUrl = apiUrl,
                AccessToken = accessToken,
                Settings = settings
            }, Json, ct), ct);

    /// <param name="accessToken">Blank keeps the stored token.</param>
    /// <param name="settings">An omitted secret keeps the stored one, as with the token.</param>
    public Task UpdateTrackerConnectionAsync(
        Guid id, string name, string apiUrl, string? accessToken,
        Dictionary<string, string?>? settings = null, CancellationToken ct = default) =>
        SendAsync(() => Http.PutAsJsonAsync($"api/tracker-connections/{id}",
            new { Name = name, ApiUrl = apiUrl, AccessToken = accessToken, Settings = settings }, Json, ct), ct);

    public Task DeleteTrackerConnectionAsync(Guid id, CancellationToken ct = default) =>
        SendAsync(() => Http.DeleteAsync($"api/tracker-connections/{id}", ct), ct);

    /// <summary>
    /// Polls a tracker connection now: the tracker is asked what is open and the tasks already known
    /// are re-read, returning how many syncs were requested and how many of them are new.
    /// </summary>
    public Task<TrackerPollResultDto> PollTrackerConnectionAsync(Guid id, CancellationToken ct = default) =>
        SendAsync<TrackerPollResultDto>(() => Http.PostAsync($"api/tracker-connections/{id}/poll", content: null, ct), ct);
}
