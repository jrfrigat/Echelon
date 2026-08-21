using System.Net.Http.Json;
using Echelon.Application.DTOs;

namespace Echelon.Pwa.Services.Api;

/// <summary>VCS connections and the providers that back them.</summary>
/// <param name="http">The client this area talks over.</param>
public sealed class VcsApi(HttpClient http) : ApiClient(http)
{
    /// <summary>
    /// The VCS providers this build registers, and the settings each declares.
    /// </summary>
    /// <remarks>
    /// The connection form is built from this rather than from a list of provider names compiled
    /// into the PWA. That list was previously a literal <c>["GitLab"]</c> next to a fixed set of
    /// fields, so a second provider meant editing the page - and a provider needing its own field
    /// had nowhere to put it.
    /// </remarks>
    public Task<List<ProviderTypeDto>> GetVcsProviderTypesAsync(CancellationToken ct = default) =>
        GetAsync<List<ProviderTypeDto>>("api/providers/vcs", ct);

    /// <summary>A page of VCS connections, narrowed by the grid's column filter boxes.</summary>
    public Task<PagedResult<VcsConnectionDto>> GetVcsConnectionsAsync(
        int page = 1, int pageSize = 50,
        string? name = null, string? type = null, string? apiUrl = null,
        CancellationToken ct = default) =>
        GetAsync<PagedResult<VcsConnectionDto>>(
            $"api/vcs-connections?{Query(("page", page), ("pageSize", pageSize), ("name", name), ("type", type), ("apiUrl", apiUrl))}", ct);

    /// <param name="settings">Provider-specific settings, keyed as that provider's schema declares them.</param>
    public Task CreateVcsConnectionAsync(
        string name, string vcsType, string apiUrl, string accessToken,
        Dictionary<string, string?>? settings = null,
        CancellationToken ct = default) =>
        SendAsync(() => Http.PostAsJsonAsync("api/vcs-connections",
            new
            {
                Name = name,
                VcsType = vcsType,
                ApiUrl = apiUrl,
                AccessToken = accessToken,
                Settings = settings
            }, Json, ct), ct);

    /// <param name="accessToken">Blank keeps the stored token.</param>
    /// <param name="settings">
    /// Provider-specific settings. An omitted secret keeps the stored one - the same convention as
    /// the token, and why this sends only the keys the operator actually filled in.
    /// </param>
    public Task UpdateVcsConnectionAsync(
        Guid id, string name, string apiUrl, string? accessToken,
        Dictionary<string, string?>? settings = null,
        CancellationToken ct = default) =>
        SendAsync(() => Http.PutAsJsonAsync($"api/vcs-connections/{id}",
            new
            {
                Name = name,
                ApiUrl = apiUrl,
                AccessToken = accessToken,
                Settings = settings
            }, Json, ct), ct);

    public Task DeleteVcsConnectionAsync(Guid id, CancellationToken ct = default) =>
        SendAsync(() => Http.DeleteAsync($"api/vcs-connections/{id}", ct), ct);

    /// <summary>Polls a poll-mode connection's open merge requests now, returning how many were emitted.</summary>
    public Task<VcsPollResultDto> PollVcsConnectionAsync(Guid id, CancellationToken ct = default) =>
        SendAsync<VcsPollResultDto>(() => Http.PostAsync($"api/vcs-connections/{id}/poll", content: null, ct), ct);
}
