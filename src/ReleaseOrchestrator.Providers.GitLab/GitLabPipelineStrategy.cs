using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using ReleaseOrchestrator.Providers.Abstractions;
using ReleaseOrchestrator.Providers.Abstractions.Deploy;

namespace ReleaseOrchestrator.Providers.GitLab;

/// <summary>
/// Deploys by triggering a GitLab pipeline (<c>POST .../pipeline</c>) and polling it to completion.
/// </summary>
/// <remarks>
/// Two-phase: <see cref="StartAsync"/> returns <see cref="DeployOutcome.Awaiting"/> with the pipeline
/// id as the external reference; the executor's watcher polls <see cref="PollAsync"/> until it
/// settles. <see cref="ReconcileAsync"/> returns null: GitLab does not tag a pipeline with our key,
/// so a resumed step cannot reliably re-find one -- a known limitation
///.
///
/// NOT VERIFIED against a live GitLab. Endpoints and status values follow the GitLab REST API.
/// </remarks>
internal sealed class GitLabPipelineStrategy(HttpClient http) : IDeployStrategy
{
    /// <inheritdoc/>
    public IReadOnlyList<ProviderSettingSchema> SettingsSchema =>
    [
        new ProviderSettingSchema(
            Key: "ref",
            Label: "Pipeline ref",
            Description: "Branch or tag to run the pipeline on. Defaults to 'main'.",
            Required: false)
    ];

    /// <inheritdoc/>
    public async Task<DeployResult> StartAsync(DeployContext context, CancellationToken ct)
    {
        var reference = context.Settings.TryGetValue("ref", out var r) && !string.IsNullOrWhiteSpace(r) ? r : "main";

        using var request = Authorized(HttpMethod.Post,
            GitLabUrls.CreatePipeline(context.ApiUrl, context.ProjectPath, reference), context);

        var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            return new DeployResult(DeployOutcome.Failed, Message: $"GitLab pipeline create returned {(int)response.StatusCode}.");

        var dto = await response.Content.ReadFromJsonAsync<PipelineDto>(cancellationToken: ct).ConfigureAwait(false);
        return dto is null
            ? new DeployResult(DeployOutcome.Failed, Message: "GitLab returned no pipeline.")
            : new DeployResult(DeployOutcome.Awaiting, ExternalRef: dto.Id.ToString(CultureInfo.InvariantCulture), Message: dto.Status);
    }

    /// <inheritdoc/>
    public async Task<DeployResult> PollAsync(DeployContext context, string externalRef, CancellationToken ct)
    {
        using var request = Authorized(HttpMethod.Get,
            GitLabUrls.Pipeline(context.ApiUrl, context.ProjectPath, externalRef), context);

        var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            return new DeployResult(DeployOutcome.Failed, externalRef, $"GitLab pipeline poll returned {(int)response.StatusCode}.");

        var dto = await response.Content.ReadFromJsonAsync<PipelineDto>(cancellationToken: ct).ConfigureAwait(false);
        return dto?.Status switch
        {
            "success" => new DeployResult(DeployOutcome.Succeeded, externalRef),
            "failed" or "canceled" or "skipped" => new DeployResult(DeployOutcome.Failed, externalRef, $"Pipeline {dto.Status}."),
            _ => new DeployResult(DeployOutcome.Awaiting, externalRef, dto?.Status)
        };
    }

    /// <inheritdoc/>
    public async Task CancelAsync(DeployContext context, string externalRef, CancellationToken ct)
    {
        using var request = Authorized(HttpMethod.Post,
            GitLabUrls.CancelPipeline(context.ApiUrl, context.ProjectPath, externalRef), context);

        await http.SendAsync(request, ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    public Task<DeployResult?> ReconcileAsync(DeployContext context, CancellationToken ct) =>
        Task.FromResult<DeployResult?>(null);

    private static HttpRequestMessage Authorized(HttpMethod method, Uri url, DeployContext context)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add("PRIVATE-TOKEN", context.AccessToken);
        return request;
    }

    private sealed record PipelineDto(
        [property: JsonPropertyName("id")] long Id,
        [property: JsonPropertyName("status")] string? Status);
}
