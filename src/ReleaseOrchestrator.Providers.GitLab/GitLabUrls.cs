namespace ReleaseOrchestrator.Providers.GitLab;

/// <summary>Builds GitLab REST API addresses.</summary>
/// <remarks>
/// One place so the <c>/api/v4</c> prefix and the escaping rules are stated once. A project is
/// addressed by its URL-encoded <c>path_with_namespace</c>, so the slashes inside it must survive
/// as <c>%2F</c> — building these by interpolation at each call site is how a path with a subgroup
/// silently turns into a 404.
/// </remarks>
internal static class GitLabUrls
{
    private static string Root(Uri apiUrl) => apiUrl.ToString().TrimEnd('/');

    internal static Uri Version(Uri apiUrl) => new($"{Root(apiUrl)}/api/v4/version");

    internal static Uri MergeRequest(Uri apiUrl, string projectPath, string iid) =>
        new($"{Root(apiUrl)}/api/v4/projects/{Uri.EscapeDataString(projectPath)}/merge_requests/{Uri.EscapeDataString(iid)}");

    internal static Uri OpenMergeRequests(Uri apiUrl, string projectPath) =>
        new($"{Root(apiUrl)}/api/v4/projects/{Uri.EscapeDataString(projectPath)}/merge_requests?state=opened&per_page=100");

    internal static Uri MergeMergeRequest(Uri apiUrl, string projectPath, string iid) =>
        new($"{Root(apiUrl)}/api/v4/projects/{Uri.EscapeDataString(projectPath)}/merge_requests/{Uri.EscapeDataString(iid)}/merge");

    internal static Uri CreatePipeline(Uri apiUrl, string projectPath, string reference) =>
        new($"{Root(apiUrl)}/api/v4/projects/{Uri.EscapeDataString(projectPath)}/pipeline?ref={Uri.EscapeDataString(reference)}");

    internal static Uri Pipeline(Uri apiUrl, string projectPath, string pipelineId) =>
        new($"{Root(apiUrl)}/api/v4/projects/{Uri.EscapeDataString(projectPath)}/pipelines/{Uri.EscapeDataString(pipelineId)}");

    internal static Uri CancelPipeline(Uri apiUrl, string projectPath, string pipelineId) =>
        new($"{Root(apiUrl)}/api/v4/projects/{Uri.EscapeDataString(projectPath)}/pipelines/{Uri.EscapeDataString(pipelineId)}/cancel");
}
