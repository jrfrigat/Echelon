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
        // Page through all open merge requests: GitLab caps per_page at 100, so reading only the first
        // page silently dropped every open MR beyond 100 from the poll-based ingestion backstop. The
        // next page number comes back in X-Next-Page, empty on the last page.
        var all = new List<VcsMergeRequest>();
        for (var page = 1; page <= MaxPages; page++)
        {
            using var request = Authorized(HttpMethod.Get, GitLabUrls.OpenMergeRequests(context.ApiUrl, projectPath, page));
            var response = await http.SendAsync(request, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var dtos = await response.Content
                .ReadFromJsonAsync<List<GitLabMrDto>>(cancellationToken: ct)
                .ConfigureAwait(false);
            if (dtos is { Count: > 0 })
                all.AddRange(dtos.Select(d => d.ToMergeRequest()));

            var next = response.Headers.TryGetValues("X-Next-Page", out var values) ? values.FirstOrDefault() : null;
            if (string.IsNullOrEmpty(next)) break;
        }

        return all;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<VcsBranch>> GetBranchesAsync(string projectPath, CancellationToken ct)
    {
        // Paged like the merge requests, and for the same reason: a repository with more than 100
        // branches would otherwise report only the first page and look like it had less unlanded work.
        var all = new List<VcsBranch>();
        for (var page = 1; page <= MaxPages; page++)
        {
            using var request = Authorized(HttpMethod.Get, GitLabUrls.Branches(context.ApiUrl, projectPath, page));
            var response = await http.SendAsync(request, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var dtos = await response.Content
                .ReadFromJsonAsync<List<GitLabBranchDto>>(cancellationToken: ct)
                .ConfigureAwait(false);
            if (dtos is { Count: > 0 })
                all.AddRange(dtos.Select(d => new VcsBranch(d.Name ?? string.Empty, d.Merged, d.Default)));

            var next = response.Headers.TryGetValues("X-Next-Page", out var values) ? values.FirstOrDefault() : null;
            if (string.IsNullOrEmpty(next)) break;
        }

        return all;
    }

    /// <summary>A safety bound so a misbehaving pagination header cannot loop forever (100 pages = 10k MRs).</summary>
    private const int MaxPages = 100;

    /// <summary>GitLab's branch shape: the name, whether it has landed, and whether it is the default.</summary>
    private sealed record GitLabBranchDto(
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("merged")] bool Merged,
        [property: JsonPropertyName("default")] bool Default);

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
        // DateTimeOffset, not DateTime: GitLab stamps an offset ("...+03:00"), and deserialising that
        // into a DateTime yields Kind=Local - which SQL Server stores without complaint, at the wrong
        // instant whenever the host is not on UTC, and which PostgreSQL refuses outright, since it maps
        // DateTime to timestamptz and Npgsql writes only Kind=Utc. So the bug is invisible until the
        // second database runs. Parse the offset, hand back UtcDateTime; the tracker adapter does the same.
        [property: JsonPropertyName("created_at")] DateTimeOffset CreatedAt,
        [property: JsonPropertyName("merged_at")] DateTimeOffset? MergedAt,
        [property: JsonPropertyName("labels")] IReadOnlyList<string>? Labels,
        // head_pipeline is part of the merge-request representation; null when the head has no pipeline.
        [property: JsonPropertyName("head_pipeline")] GitLabPipelineDto? HeadPipeline)
    {
        internal VcsMergeRequest ToMergeRequest() => new(
            Id: Iid.ToString(System.Globalization.CultureInfo.InvariantCulture),
            SourceBranch: SourceBranch ?? string.Empty,
            TargetBranch: TargetBranch ?? string.Empty,
            // Resolved here, at the edge: the raw state never travels further in.
            Status: GitLabMergeRequestState.FromState(State),
            Title: Title ?? string.Empty,
            CreatedAt: CreatedAt.UtcDateTime,
            MergedAt: MergedAt?.UtcDateTime,
            Labels: Labels ?? [],
            PipelineStatus: HeadPipeline?.Status);
    }

    private sealed record GitLabPipelineDto(
        [property: JsonPropertyName("status")] string? Status);
}
