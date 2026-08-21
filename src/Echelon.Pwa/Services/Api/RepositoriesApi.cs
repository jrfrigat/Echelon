using System.Net.Http.Json;
using Echelon.Application.DTOs;

namespace Echelon.Pwa.Services.Api;

/// <summary>The repositories the service watches.</summary>
/// <param name="http">The client this area talks over.</param>
public sealed class RepositoriesApi(HttpClient http) : ApiClient(http)
{
    /// <summary>A page of repositories, narrowed by the grid's column filter boxes.</summary>
    public Task<PagedResult<RepositoryDto>> GetRepositoriesAsync(
        int page = 1, int pageSize = 50,
        string? name = null, string? externalId = null, string? connection = null,
        CancellationToken ct = default) =>
        GetAsync<PagedResult<RepositoryDto>>(
            $"api/repositories?{Query(("page", page), ("pageSize", pageSize), ("name", name), ("externalId", externalId), ("connection", connection))}", ct);

    public Task CreateRepositoryAsync(string name, string externalId, Guid connectionId, CancellationToken ct = default) =>
        SendAsync(() => Http.PostAsJsonAsync("api/repositories",
            new { Name = name, ExternalId = externalId, ConnectionId = connectionId }, Json, ct), ct);

    public Task DeleteRepositoryAsync(Guid id, CancellationToken ct = default) =>
        SendAsync(() => Http.DeleteAsync($"api/repositories/{id}", ct), ct);
}
