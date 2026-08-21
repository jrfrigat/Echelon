using System.Net.Http.Json;
using Echelon.Application.DTOs;
using Echelon.Core.Enums;

namespace Echelon.Pwa.Services.Api;

/// <summary>Readiness rules, and the pins that override them.</summary>
/// <param name="http">The client this area talks over.</param>
public sealed class ReadinessApi(HttpClient http) : ApiClient(http)
{
    public Task<List<ReadinessRuleDto>> GetReadinessRulesAsync(CancellationToken ct = default) =>
        GetAsync<List<ReadinessRuleDto>>("api/readiness-rules", ct);

    /// <param name="mode">"AnyOf" or "AllOf".</param>
    /// <param name="requiredSignals">The signal tokens a merge request must carry, e.g. label:ready-for-prod.</param>
    public Task CreateReadinessRuleAsync(
        string name, ReadyRule mode, IReadOnlyList<string> requiredSignals, CancellationToken ct = default) =>
        SendAsync(() => Http.PostAsJsonAsync("api/readiness-rules",
            new { Name = name, Mode = mode, RequiredSignals = requiredSignals }, Json, ct), ct);

    public Task UpdateReadinessRuleAsync(
        Guid id, string name, ReadyRule mode, IReadOnlyList<string> requiredSignals, CancellationToken ct = default) =>
        SendAsync(() => Http.PutAsJsonAsync($"api/readiness-rules/{id}",
            new { Name = name, Mode = mode, RequiredSignals = requiredSignals }, Json, ct), ct);

    public Task DeleteReadinessRuleAsync(Guid id, CancellationToken ct = default) =>
        SendAsync(() => Http.DeleteAsync($"api/readiness-rules/{id}", ct), ct);

    public Task<List<ReadinessPinDto>> GetReadinessPinsAsync(Guid mergeRequestId, CancellationToken ct = default) =>
        GetAsync<List<ReadinessPinDto>>($"api/readiness-pins?mergeRequestId={mergeRequestId}", ct);

    /// <param name="isReady">True admits the merge request into the environment; false holds it out.</param>
    public Task SetReadinessPinAsync(
        Guid mergeRequestId, Guid environmentId, bool isReady, string? reason = null, CancellationToken ct = default) =>
        SendAsync(() => Http.PutAsJsonAsync("api/readiness-pins",
            new { MergeRequestId = mergeRequestId, EnvironmentId = environmentId, IsReady = isReady, Reason = reason }, Json, ct), ct);

    public Task DeleteReadinessPinAsync(Guid id, CancellationToken ct = default) =>
        SendAsync(() => Http.DeleteAsync($"api/readiness-pins/{id}", ct), ct);
}
