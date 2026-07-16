using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using ReleaseOrchestrator.Application.Services;

namespace ReleaseOrchestrator.Infrastructure.Tracker;

public class YandexTrackerApiClient(HttpClient http) : ITrackerApiClient
{
    public async Task<TrackerIssueInfo?> GetIssueAsync(string apiUrl, string orgId, string token, string issueKey, CancellationToken ct)
    {
        using var req = BuildRequest(HttpMethod.Get, $"{apiUrl}/v2/issues/{issueKey}", orgId, token);
        var resp = await http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode) return null;

        var dto = await resp.Content.ReadFromJsonAsync<YtIssueDto>(cancellationToken: ct);
        return dto is null ? null : new TrackerIssueInfo(dto.Key, dto.Summary, dto.Status.Key, dto.ResolvedAt);
    }

    public async Task<IReadOnlyList<TrackerIssueDependency>> GetIssueDependenciesAsync(string apiUrl, string orgId, string token, string issueKey, CancellationToken ct)
    {
        using var req = BuildRequest(HttpMethod.Get, $"{apiUrl}/v2/issues/{issueKey}/links", orgId, token);
        var resp = await http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode) return [];

        var dtos = await resp.Content.ReadFromJsonAsync<List<YtIssueLinkDto>>(cancellationToken: ct);
        return dtos?
            .Where(d => d.Type.Id == "depends")
            .Select(d => new TrackerIssueDependency(issueKey, d.Object.Key))
            .ToList() ?? [];
    }

    private static HttpRequestMessage BuildRequest(HttpMethod method, string url, string orgId, string token)
    {
        var req = new HttpRequestMessage(method, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("OAuth", token);
        req.Headers.Add("X-Org-Id", orgId);
        return req;
    }

    private record YtIssueDto(
        [property: JsonPropertyName("key")] string Key,
        [property: JsonPropertyName("summary")] string Summary,
        [property: JsonPropertyName("status")] YtStatus Status,
        [property: JsonPropertyName("resolvedAt")] DateTime? ResolvedAt);

    private record YtStatus([property: JsonPropertyName("key")] string Key);

    private record YtIssueLinkDto(
        [property: JsonPropertyName("type")] YtLinkType Type,
        [property: JsonPropertyName("object")] YtLinkObject Object);

    private record YtLinkType([property: JsonPropertyName("id")] string Id);
    private record YtLinkObject([property: JsonPropertyName("key")] string Key);
}
