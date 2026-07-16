using System.Net.Http.Json;
using System.Text.Json.Serialization;
using ReleaseOrchestrator.Application.Services;

namespace ReleaseOrchestrator.Infrastructure.Vcs;

public class GitLabApiClient(HttpClient http) : IVcsApiClient
{
    public async Task<VcsApiMrInfo?> GetMergeRequestAsync(string apiUrl, string token, string projectPath, string iid, CancellationToken ct)
    {
        var encodedPath = Uri.EscapeDataString(projectPath);
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{apiUrl}/api/v4/projects/{encodedPath}/merge_requests/{iid}");
        req.Headers.Add("PRIVATE-TOKEN", token);

        var resp = await http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode) return null;

        var dto = await resp.Content.ReadFromJsonAsync<GitLabMrDto>(cancellationToken: ct);
        return dto?.ToApiInfo();
    }

    public async Task<IReadOnlyList<VcsApiMrInfo>> GetOpenMergeRequestsAsync(string apiUrl, string token, string projectPath, CancellationToken ct)
    {
        var encodedPath = Uri.EscapeDataString(projectPath);
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{apiUrl}/api/v4/projects/{encodedPath}/merge_requests?state=opened&per_page=100");
        req.Headers.Add("PRIVATE-TOKEN", token);

        var resp = await http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();

        var dtos = await resp.Content.ReadFromJsonAsync<List<GitLabMrDto>>(cancellationToken: ct);
        return dtos?.Select(d => d.ToApiInfo()).ToList() ?? [];
    }

    private record GitLabMrDto(
        [property: JsonPropertyName("iid")] int Iid,
        [property: JsonPropertyName("source_branch")] string SourceBranch,
        [property: JsonPropertyName("target_branch")] string TargetBranch,
        [property: JsonPropertyName("state")] string State,
        [property: JsonPropertyName("title")] string Title,
        [property: JsonPropertyName("created_at")] DateTime CreatedAt,
        [property: JsonPropertyName("merged_at")] DateTime? MergedAt)
    {
        public VcsApiMrInfo ToApiInfo() => new(Iid.ToString(), SourceBranch, TargetBranch, State, Title, CreatedAt, MergedAt);
    }
}
