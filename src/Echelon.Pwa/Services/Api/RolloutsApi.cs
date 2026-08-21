using System.Net.Http.Json;
using Echelon.Application.DTOs;

namespace Echelon.Pwa.Services.Api;

/// <summary>Launching a rollout and steering it once it runs.</summary>
/// <param name="http">The client this area talks over.</param>
public sealed class RolloutsApi(HttpClient http) : ApiClient(http)
{
    /// <param name="redeploy">Redeploy already-deployed merge requests, where their target permits it.</param>
    public Task<RolloutDto> LaunchRolloutAsync(Guid taskId, Guid environmentId, bool redeploy = false, CancellationToken ct = default) =>
        SendAsync<RolloutDto>(
            () => Http.PostAsJsonAsync($"api/tasks/{taskId}/rollouts", new { EnvironmentId = environmentId, Redeploy = redeploy }, Json, ct), ct);

    public Task<RolloutDto?> GetRolloutAsync(Guid id, CancellationToken ct = default) =>
        GetOrNullAsync<RolloutDto>($"api/rollouts/{id}", ct);

    public Task<List<RolloutSummaryDto>> GetRolloutsAsync(Guid? taskId = null, CancellationToken ct = default) =>
        GetAsync<List<RolloutSummaryDto>>($"api/rollouts{(taskId is null ? "" : $"?taskId={taskId}")}", ct);

    public Task CancelRolloutAsync(Guid id, CancellationToken ct = default) =>
        SendAsync(() => Http.PostAsync($"api/rollouts/{id}/cancel", null, ct), ct);

    public Task RetryRolloutStepAsync(Guid rolloutId, Guid stepId, CancellationToken ct = default) =>
        SendAsync(() => Http.PostAsync($"api/rollouts/{rolloutId}/steps/{stepId}/retry", null, ct), ct);

    public Task SkipRolloutStepAsync(Guid rolloutId, Guid stepId, CancellationToken ct = default) =>
        SendAsync(() => Http.PostAsync($"api/rollouts/{rolloutId}/steps/{stepId}/skip", null, ct), ct);
}
