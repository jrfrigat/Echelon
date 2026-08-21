using Echelon.Application.DTOs;

namespace Echelon.Pwa.Services.Api;

/// <summary>The request audit and its summary.</summary>
/// <param name="http">The client this area talks over.</param>
public sealed class AuditApi(HttpClient http) : ApiClient(http)
{
    public Task<PagedResult<RequestAuditEntryDto>> GetRequestAuditAsync(
        int minutes, string? status, bool notableOnly, bool includeAuditTraffic, string? search,
        string? method = null, string? path = null, string? user = null,
        int page = 1, int pageSize = 50, CancellationToken ct = default) =>
        GetAsync<PagedResult<RequestAuditEntryDto>>(
            $"api/request-audit?{Query(
                ("minutes", minutes), ("status", status), ("notableOnly", notableOnly),
                ("includeAuditTraffic", includeAuditTraffic), ("search", search),
                ("method", method), ("path", path), ("user", user),
                ("page", page), ("pageSize", pageSize))}", ct);

    public Task<RequestAuditSummaryDto> GetRequestAuditSummaryAsync(int minutes, CancellationToken ct = default) =>
        GetAsync<RequestAuditSummaryDto>($"api/request-audit/summary?minutes={minutes}", ct);
}
