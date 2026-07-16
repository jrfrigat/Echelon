using System.Net.Http.Json;
using System.Text.Json.Serialization;
using ReleaseOrchestrator.Providers.Abstractions.Vcs;

namespace ReleaseOrchestrator.Providers.GitLab;

/// <summary>
/// GitLab, bound to one connection.
/// </summary>
/// <remarks>
/// Built by <see cref="GitLabProviderAdapter"/>, which has already settled the endpoint,
/// credentials and server version. The token is applied per request rather than held on the
/// <see cref="HttpClient"/>: the client comes from the shared factory pool and may serve other
/// connections, so a header set on it would leak one connection's token to another's request.
/// </remarks>
internal sealed class GitLabProvider(
    HttpClient http,
    VcsProviderContext context,
    VcsCapabilities capabilities) : IVcsProvider
{
    /// <inheritdoc/>
    public VcsCapabilities Capabilities => capabilities;

    /// <inheritdoc/>
    public string? ParseTaskKeyFromBranch(string? branchName) =>
        GitLabBranchTaskParser.ParseTaskId(branchName);

    /// <inheritdoc/>
    public async Task<VcsMergeRequest?> GetMergeRequestAsync(string projectPath, string mergeRequestId, CancellationToken ct)
    {
        using var request = Authorized(HttpMethod.Get, GitLabUrls.MergeRequest(context.ApiUrl, projectPath, mergeRequestId));

        var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return null;

        var dto = await response.Content.ReadFromJsonAsync<GitLabMrDto>(cancellationToken: ct).ConfigureAwait(false);
        return dto?.ToMergeRequest();
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<VcsMergeRequest>> GetOpenMergeRequestsAsync(string projectPath, CancellationToken ct)
    {
        using var request = Authorized(HttpMethod.Get, GitLabUrls.OpenMergeRequests(context.ApiUrl, projectPath));

        var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var dtos = await response.Content
            .ReadFromJsonAsync<List<GitLabMrDto>>(cancellationToken: ct)
            .ConfigureAwait(false);

        return dtos?.Select(d => d.ToMergeRequest()).ToList() ?? [];
    }

    private HttpRequestMessage Authorized(HttpMethod method, Uri url)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add("PRIVATE-TOKEN", context.AccessToken);
        return request;
    }

    private sealed record GitLabMrDto(
        [property: JsonPropertyName("iid")] int Iid,
        [property: JsonPropertyName("source_branch")] string? SourceBranch,
        [property: JsonPropertyName("target_branch")] string? TargetBranch,
        [property: JsonPropertyName("state")] string? State,
        [property: JsonPropertyName("title")] string? Title,
        [property: JsonPropertyName("created_at")] DateTime CreatedAt,
        [property: JsonPropertyName("merged_at")] DateTime? MergedAt,
        [property: JsonPropertyName("labels")] IReadOnlyList<string>? Labels)
    {
        internal VcsMergeRequest ToMergeRequest() => new(
            Id: Iid.ToString(System.Globalization.CultureInfo.InvariantCulture),
            SourceBranch: SourceBranch ?? string.Empty,
            TargetBranch: TargetBranch ?? string.Empty,
            // Resolved here, at the edge: the raw state never travels further in.
            Status: GitLabMergeRequestState.FromState(State),
            Title: Title ?? string.Empty,
            CreatedAt: CreatedAt,
            MergedAt: MergedAt,
            Labels: Labels ?? []);
    }
}
