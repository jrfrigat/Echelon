using System.Net.Http.Json;
using Echelon.Application.DTOs;

namespace Echelon.Pwa.Services.Api;

/// <summary>The ordering rules - as a document, as a structure, and as the default plan they produce.</summary>
/// <param name="http">The client this area talks over.</param>
public sealed class PlanningApi(HttpClient http) : ApiClient(http)
{
    /// <summary>The ordering-rule document as stored.</summary>
    public Task<OrderingRulesDocumentDto> GetOrderingRulesAsync(CancellationToken ct = default) =>
        GetAsync<OrderingRulesDocumentDto>("api/planning/rules", ct);

    /// <summary>Checks a document without saving, reporting problems and what each group selects.</summary>
    public Task<OrderingRulesValidationDto> ValidateOrderingRulesAsync(string document, CancellationToken ct = default) =>
        SendAsync<OrderingRulesValidationDto>(
            () => Http.PostAsJsonAsync("api/planning/rules/validate", new { Document = document }, Json, ct), ct);

    /// <summary>Saves the document. An invalid one is refused with the problems listed.</summary>
    public Task SaveOrderingRulesAsync(string document, CancellationToken ct = default) =>
        SendAsync(() => Http.PutAsJsonAsync("api/planning/rules", new { Document = document }, Json, ct), ct);

    /// <summary>The rules configured on screen, written out as a document ready to adopt.</summary>
    public Task<OrderingRulesDocumentDto> OrderingRulesFromScreenAsync(CancellationToken ct = default) =>
        GetAsync<OrderingRulesDocumentDto>("api/planning/rules/from-repository-ordering", ct);

    public Task<PagedResult<RepositoryOrderingDto>> GetRepositoryOrderingAsync(int page = 1, CancellationToken ct = default) =>
        GetAsync<PagedResult<RepositoryOrderingDto>>($"api/repository-ordering?page={page}&pageSize=50", ct);

    /// <summary>The order those rules add up to, derived server-side by the real ordering engine.</summary>
    public Task<DefaultPlanDto> GetDefaultPlanAsync(CancellationToken ct = default) =>
        GetAsync<DefaultPlanDto>("api/repository-ordering/plan", ct);

    /// <param name="fromRepositoryId">The repository that deploys later.</param>
    /// <param name="toRepositoryId">The repository that deploys first.</param>
    /// <param name="type">"Hard" (never dropped) or "Soft" (dropped first to break a cycle).</param>
    public Task CreateRepositoryOrderingAsync(
        Guid fromRepositoryId, Guid toRepositoryId, string type, CancellationToken ct = default) =>
        SendAsync(() => Http.PostAsJsonAsync("api/repository-ordering",
            new { FromRepositoryId = fromRepositoryId, ToRepositoryId = toRepositoryId, Type = type }, Json, ct), ct);

    public Task DeleteRepositoryOrderingAsync(Guid id, CancellationToken ct = default) =>
        SendAsync(() => Http.DeleteAsync($"api/repository-ordering/{id}", ct), ct);

    /// <summary>The ordering rules as a structure, for the visual editor.</summary>
    /// <remarks>
    /// Parsed on the server. Doing it here would mean a second YAML reader deciding what a document
    /// means, and the planner's is the one that counts.
    /// </remarks>
    public Task<OrderingRulesModelDto> GetOrderingRulesModelAsync(CancellationToken ct = default) =>
        GetAsync<OrderingRulesModelDto>("api/planning/rules/model", ct);

    /// <summary>Renders a structure into document text without saving it.</summary>
    public Task<OrderingRulesDocumentDto> RenderOrderingRulesModelAsync(
        OrderingRulesModelDto model, CancellationToken ct = default) =>
        SendAsync<OrderingRulesDocumentDto>(
            () => Http.PostAsJsonAsync("api/planning/rules/model/render", model, Json, ct), ct);

    /// <summary>Renders a structure and stores it as the ordering-rule document.</summary>
    public Task SaveOrderingRulesModelAsync(OrderingRulesModelDto model, CancellationToken ct = default) =>
        SendAsync(() => Http.PutAsJsonAsync("api/planning/rules/model", model, Json, ct), ct);
}
