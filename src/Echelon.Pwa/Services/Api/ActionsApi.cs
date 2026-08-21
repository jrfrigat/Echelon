using System.Net.Http.Json;
using Echelon.Application.DTOs;

namespace Echelon.Pwa.Services.Api;

/// <summary>Action bindings and the handlers they can call.</summary>
/// <param name="http">The client this area talks over.</param>
public sealed class ActionsApi(HttpClient http) : ApiClient(http)
{
    public Task<List<ActionBindingDto>> GetActionBindingsAsync(CancellationToken ct = default) =>
        GetAsync<List<ActionBindingDto>>("api/action-bindings", ct);

    public Task<List<ActionTypeDto>> GetActionTypesAsync(CancellationToken ct = default) =>
        GetAsync<List<ActionTypeDto>>("api/action-bindings/types", ct);

    public Task CreateActionBindingAsync(
        string eventType, string actionType, string? scope, Dictionary<string, string> settings, int order, bool enabled,
        CancellationToken ct = default) =>
        SendAsync(() => Http.PostAsJsonAsync("api/action-bindings",
            new { EventType = eventType, ActionType = actionType, Scope = scope, Settings = settings, Order = order, Enabled = enabled }, Json, ct), ct);

    public Task DeleteActionBindingAsync(Guid id, CancellationToken ct = default) =>
        SendAsync(() => Http.DeleteAsync($"api/action-bindings/{id}", ct), ct);
}
