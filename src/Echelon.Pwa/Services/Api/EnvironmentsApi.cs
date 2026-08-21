using System.Net.Http.Json;
using Echelon.Application.DTOs;

namespace Echelon.Pwa.Services.Api;

/// <summary>The environments a rollout deploys into.</summary>
/// <param name="http">The client this area talks over.</param>
public sealed class EnvironmentsApi(HttpClient http) : ApiClient(http)
{
    public Task<List<EnvironmentDto>> GetEnvironmentsAsync(CancellationToken ct = default) =>
        GetAsync<List<EnvironmentDto>>("api/environments", ct);

    /// <param name="readinessRuleId">The environment's default readiness rule, or null for no gate (needs approval).</param>
    public Task CreateEnvironmentAsync(
        string key, string name, int order, bool isEnabled,
        Guid? readinessRuleId = null, CancellationToken ct = default) =>
        SendAsync(() => Http.PostAsJsonAsync("api/environments",
            new { Key = key, Name = name, Order = order, IsEnabled = isEnabled, ReadinessRuleId = readinessRuleId }, Json, ct), ct);

    /// <param name="readinessRuleId">The default readiness rule, or null for no gate; removing a gate needs approval.</param>
    public Task UpdateEnvironmentAsync(
        Guid id, string name, int order, bool isEnabled,
        Guid? readinessRuleId = null, CancellationToken ct = default) =>
        SendAsync(() => Http.PutAsJsonAsync($"api/environments/{id}",
            new { Key = "", Name = name, Order = order, IsEnabled = isEnabled, ReadinessRuleId = readinessRuleId }, Json, ct), ct);

    public Task DeleteEnvironmentAsync(Guid id, CancellationToken ct = default) =>
        SendAsync(() => Http.DeleteAsync($"api/environments/{id}", ct), ct);
}
