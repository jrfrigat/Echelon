using System.Net.Http.Json;
using Echelon.Application.DTOs;
using Echelon.Core.Enums;

namespace Echelon.Pwa.Services.Api;

/// <summary>Deploy targets and the strategies available to them.</summary>
/// <param name="http">The client this area talks over.</param>
public sealed class DeployApi(HttpClient http) : ApiClient(http)
{
    /// <summary>The deploy strategies this build registers, and the settings each declares.</summary>
    public Task<List<ProviderTypeDto>> GetDeployStrategiesAsync(CancellationToken ct = default) =>
        GetAsync<List<ProviderTypeDto>>("api/providers/deploy-strategies", ct);

    public Task<List<DeployTargetDto>> GetDeployTargetsAsync(CancellationToken ct = default) =>
        GetAsync<List<DeployTargetDto>>("api/deploy-targets", ct);

    /// <param name="settings">Strategy settings, keyed as the strategy declares them.</param>
    /// <param name="readinessRuleId">Readiness-rule override for this pair, or null to use the environment default.</param>
    public Task CreateDeployTargetAsync(
        Guid repositoryId, Guid environmentId, string deployStrategyKey, RedeployPolicy redeployPolicy,
        Dictionary<string, string?>? settings = null, Guid? readinessRuleId = null, CancellationToken ct = default) =>
        SendAsync(() => Http.PostAsJsonAsync("api/deploy-targets",
            new
            {
                RepositoryId = repositoryId,
                EnvironmentId = environmentId,
                DeployStrategyKey = deployStrategyKey,
                RedeployPolicy = redeployPolicy,
                Settings = settings,
                ReadinessRuleId = readinessRuleId
            }, Json, ct), ct);

    /// <param name="settings">An omitted secret keeps the stored one, as with a connection token.</param>
    /// <param name="readinessRuleId">Readiness-rule override for this pair, or null to use the environment default.</param>
    public Task UpdateDeployTargetAsync(
        Guid id, Guid repositoryId, Guid environmentId, string deployStrategyKey, RedeployPolicy redeployPolicy,
        Dictionary<string, string?>? settings = null, Guid? readinessRuleId = null, CancellationToken ct = default) =>
        SendAsync(() => Http.PutAsJsonAsync($"api/deploy-targets/{id}",
            new
            {
                RepositoryId = repositoryId,
                EnvironmentId = environmentId,
                DeployStrategyKey = deployStrategyKey,
                RedeployPolicy = redeployPolicy,
                Settings = settings,
                ReadinessRuleId = readinessRuleId
            }, Json, ct), ct);

    public Task DeleteDeployTargetAsync(Guid id, CancellationToken ct = default) =>
        SendAsync(() => Http.DeleteAsync($"api/deploy-targets/{id}", ct), ct);
}
