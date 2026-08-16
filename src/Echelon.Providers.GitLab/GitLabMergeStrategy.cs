using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Echelon.Providers.Abstractions;
using Echelon.Providers.Abstractions.Deploy;

namespace Echelon.Providers.GitLab;

/// <summary>
/// Deploys by merging the merge request (GitLab <c>PUT .../merge</c>).
/// </summary>
/// <remarks>
/// Idempotent as the contract requires: an already-merged request returns
/// <see cref="DeployOutcome.AlreadyDone"/> rather than erroring. The token is applied per request,
/// not on the pooled <see cref="HttpClient"/>, so it cannot leak across connections.
///
/// NOT VERIFIED against a live GitLab: no instance was reachable. The endpoints and the merged-state
/// check follow the GitLab REST API; confirm before trusting in production.
/// </remarks>
internal sealed class GitLabMergeStrategy(HttpClient http) : IDeployStrategy
{
    /// <inheritdoc/>
    public IReadOnlyList<ProviderSettingSchema> SettingsSchema => [];

    /// <inheritdoc/>
    public async Task<DeployResult> StartAsync(DeployContext context, CancellationToken ct)
    {
        // Already merged is success, not a failure -- that is what makes a redelivered step safe.
        if (await IsMergedAsync(context, ct).ConfigureAwait(false))
            return new DeployResult(DeployOutcome.AlreadyDone);

        using var request = Authorized(HttpMethod.Put,
            GitLabUrls.MergeMergeRequest(context.ApiUrl, context.ProjectPath, context.MergeRequestExternalId), context);

        var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        return response.IsSuccessStatusCode
            ? new DeployResult(DeployOutcome.Succeeded, ExternalRef: context.MergeRequestExternalId)
            : new DeployResult(DeployOutcome.Failed, Message: $"GitLab merge returned {(int)response.StatusCode}.");
    }

    /// <inheritdoc/>
    public async Task<DeployResult> PollAsync(DeployContext context, string externalRef, CancellationToken ct) =>
        await IsMergedAsync(context, ct).ConfigureAwait(false)
            ? new DeployResult(DeployOutcome.Succeeded)
            : new DeployResult(DeployOutcome.Failed, Message: "Merge request is not merged.");

    /// <inheritdoc/>
    public Task CancelAsync(DeployContext context, string externalRef, CancellationToken ct) =>
        Task.CompletedTask; // A merge cannot be un-done.

    /// <inheritdoc/>
    public async Task<DeployResult?> ReconcileAsync(DeployContext context, CancellationToken ct) =>
        await IsMergedAsync(context, ct).ConfigureAwait(false)
            ? new DeployResult(DeployOutcome.AlreadyDone)
            : null;

    private async Task<bool> IsMergedAsync(DeployContext context, CancellationToken ct)
    {
        using var request = Authorized(HttpMethod.Get,
            GitLabUrls.MergeRequest(context.ApiUrl, context.ProjectPath, context.MergeRequestExternalId), context);

        var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return false;

        var dto = await response.Content.ReadFromJsonAsync<StateDto>(cancellationToken: ct).ConfigureAwait(false);
        return string.Equals(dto?.State, "merged", StringComparison.Ordinal);
    }

    private static HttpRequestMessage Authorized(HttpMethod method, Uri url, DeployContext context)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add("PRIVATE-TOKEN", context.AccessToken);
        return request;
    }

    private sealed record StateDto([property: JsonPropertyName("state")] string? State);
}
