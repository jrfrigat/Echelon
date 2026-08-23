using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Echelon.Providers.Abstractions;
using Echelon.Providers.Abstractions.Deploy;

namespace Echelon.Providers.GitLab;

/// <summary>
/// Deploys by running one named job of the merge request's own latest pipeline.
/// </summary>
/// <remarks>
/// <para>
/// The third deploy shape, and the one a manual-gate workflow actually asks for: the pipeline
/// already ran and already built the artefact, and deploying means pressing the one button on it -
/// <c>deploy:staging</c>. Neither sibling does that. <see cref="GitLabMergeStrategy"/> merges, which
/// is a different act; <see cref="GitLabPipelineStrategy"/> starts a <em>second</em> pipeline from a
/// ref, which rebuilds everything and runs against the branch head rather than against the commit
/// the tested pipeline was built from.
/// </para>
/// <para>
/// Two-phase like the pipeline strategy: the job is started, its id comes back as the external
/// reference, and the watcher polls until it settles. Unlike that strategy, this one reconciles:
/// the job is addressed by name inside the merge request's own pipeline, so a resumed step re-finds
/// exactly the job it started instead of being unable to tell one pipeline from another.
/// </para>
/// <para>
/// What is done to the job depends on the state it is in, because GitLab has two different verbs
/// for it: a job waiting on its manual gate is <c>play</c>ed, and one that has already run is
/// <c>retry</c>ed, which produces a new job with a new id. Getting this wrong is not a no-op -
/// playing a finished job answers "Unplayable Job" and the deploy fails for a reason that says
/// nothing about the deploy.
/// </para>
/// <para>
/// NOT VERIFIED against a live GitLab: no instance was reachable. The endpoints and the job status
/// vocabulary follow the GitLab REST API; confirm before trusting in production.
/// </para>
/// </remarks>
internal sealed class GitLabJobStrategy(HttpClient http) : IDeployStrategy
{
    /// <summary>Settings key naming the job to run.</summary>
    internal const string JobKey = "job";

    /// <summary>Settings key deciding what happens when the job already succeeded.</summary>
    internal const string RerunKey = "rerun";

    /// <summary>Re-run only when the last run was not a success. The default.</summary>
    internal const string RerunIfNotSuccessful = "if-not-successful";

    /// <summary>Re-run even a successful job, for a target whose redeploy is meant to redeploy.</summary>
    internal const string RerunAlways = "always";

    // A pipeline with more than a hundred jobs is a monorepo, not a mistake; the cap is only there so
    // a paging bug cannot spin forever.
    private const int MaxPages = 20;

    /// <inheritdoc/>
    public IReadOnlyList<ProviderSettingSchema> SettingsSchema =>
    [
        new ProviderSettingSchema(
            Key: JobKey,
            Label: "Job name",
            Description: "The job to run in the merge request's latest pipeline, spelled exactly as in "
                + ".gitlab-ci.yml - for example 'deploy:staging'.",
            Required: true),
        new ProviderSettingSchema(
            Key: RerunKey,
            Label: "Re-run",
            Description: "What to do when that job already succeeded. 'if-not-successful' treats it as "
                + "already deployed; 'always' runs it again, which is what a redeploy of an idempotent "
                + "deploy job means.",
            Kind: ProviderSettingKind.Enum,
            Options: [RerunIfNotSuccessful, RerunAlways],
            Default: RerunIfNotSuccessful)
    ];

    /// <inheritdoc/>
    public async Task<DeployResult> StartAsync(DeployContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (Setting(context, JobKey) is not { } jobName)
            return Failed($"The '{JobKey}' setting is required: name the job to run in the merge request's pipeline.");

        var (job, error) = await FindAsync(context, jobName, ct).ConfigureAwait(false);
        if (job is null) return Failed(error);

        var status = job.Status ?? string.Empty;
        var reference = Ref(job.Id);

        // Idempotency, as the contract requires: a job that has already succeeded is a deploy that
        // has already happened. A target that means to deploy again says so with 'rerun'.
        if (IsSuccess(status) && Setting(context, RerunKey) != RerunAlways)
            return new DeployResult(DeployOutcome.AlreadyDone, reference);

        // Somebody (or an earlier attempt) already set it going. Adopt it: starting a second run of a
        // deploy job is the one outcome worse than not starting one.
        if (IsInFlight(status))
            return new DeployResult(DeployOutcome.Awaiting, reference, status);

        var playable = IsPlayable(status);
        var url = playable
            ? GitLabUrls.PlayJob(context.ApiUrl, context.ProjectPath, reference)
            : GitLabUrls.RetryJob(context.ApiUrl, context.ProjectPath, reference);

        using var request = Authorized(HttpMethod.Post, url, context);
        var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            return Failed($"GitLab job {(playable ? "play" : "retry")} returned {(int)response.StatusCode} "
                + $"for '{jobName}' (job {reference}, status '{status}').");

        // A retry answers with a NEW job carrying a new id; a play answers with the same one. Either
        // way the id in the response is the job to poll - reusing the old one would watch a job that
        // will never move again.
        var started = await response.Content.ReadFromJsonAsync<JobDto>(cancellationToken: ct).ConfigureAwait(false);
        return new DeployResult(
            DeployOutcome.Awaiting,
            started is null ? reference : Ref(started.Id),
            started?.Status ?? status);
    }

    /// <inheritdoc/>
    public async Task<DeployResult> PollAsync(DeployContext context, string externalRef, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        using var request = Authorized(HttpMethod.Get, GitLabUrls.Job(context.ApiUrl, context.ProjectPath, externalRef), context);
        var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            return new DeployResult(DeployOutcome.Failed, externalRef, $"GitLab job poll returned {(int)response.StatusCode}.");

        var dto = await response.Content.ReadFromJsonAsync<JobDto>(cancellationToken: ct).ConfigureAwait(false);
        var status = dto?.Status ?? string.Empty;

        if (IsSuccess(status)) return new DeployResult(DeployOutcome.Succeeded, externalRef);
        if (IsInFlight(status)) return new DeployResult(DeployOutcome.Awaiting, externalRef, status);

        // A job back on its manual gate was never actually started, and polling it would wait for a
        // press nobody is going to make. Said as a failure rather than waited on forever.
        return IsPlayable(status)
            ? new DeployResult(DeployOutcome.Failed, externalRef, $"Job is '{status}': it is waiting to be started and the run did not take.")
            : new DeployResult(DeployOutcome.Failed, externalRef, $"Job {(status.Length == 0 ? "has no status" : $"'{status}'")}.");
    }

    /// <inheritdoc/>
    public async Task CancelAsync(DeployContext context, string externalRef, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        using var request = Authorized(HttpMethod.Post, GitLabUrls.CancelJob(context.ApiUrl, context.ProjectPath, externalRef), context);
        await http.SendAsync(request, ct).ConfigureAwait(false);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Genuinely re-findable, which the pipeline strategy is not: the job is identified by name within
    /// the merge request's latest pipeline, so a step resumed after a crash re-attaches to the run it
    /// started rather than starting a second one.
    /// </remarks>
    public async Task<DeployResult?> ReconcileAsync(DeployContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (Setting(context, JobKey) is not { } jobName) return null;

        var (job, _) = await FindAsync(context, jobName, ct).ConfigureAwait(false);
        if (job is null) return null;

        var status = job.Status ?? string.Empty;
        var reference = Ref(job.Id);

        // A success found here is our earlier attempt's success, whatever 'rerun' says: reconciling
        // asks what already happened, not what a fresh deploy would do.
        if (IsSuccess(status)) return new DeployResult(DeployOutcome.AlreadyDone, reference);
        if (IsInFlight(status)) return new DeployResult(DeployOutcome.Awaiting, reference, status);

        return null;
    }

    /// <summary>Finds the named job in the merge request's newest pipeline.</summary>
    /// <returns>The job, or the sentence explaining why there is none.</returns>
    private async Task<(JobDto? Job, string Error)> FindAsync(DeployContext context, string jobName, CancellationToken ct)
    {
        using var pipelinesRequest = Authorized(
            HttpMethod.Get,
            GitLabUrls.MergeRequestPipelines(context.ApiUrl, context.ProjectPath, context.MergeRequestExternalId),
            context);

        var pipelinesResponse = await http.SendAsync(pipelinesRequest, ct).ConfigureAwait(false);
        if (!pipelinesResponse.IsSuccessStatusCode)
            return (null, $"GitLab returned {(int)pipelinesResponse.StatusCode} listing the pipelines of merge request "
                + $"!{context.MergeRequestExternalId}.");

        var pipelines = await pipelinesResponse.Content
            .ReadFromJsonAsync<List<PipelineDto>>(cancellationToken: ct)
            .ConfigureAwait(false);

        // Highest id rather than first: the endpoint is documented as newest-first, and depending on
        // that order is a silent way to deploy the wrong commit's pipeline if it ever changes.
        if (pipelines is not { Count: > 0 })
            return (null, $"Merge request !{context.MergeRequestExternalId} has no pipeline, so there is no '{jobName}' to run.");

        var pipelineId = Ref(pipelines.Max(p => p.Id));

        var jobs = new List<JobDto>();
        for (var page = 1; page <= MaxPages; page++)
        {
            using var jobsRequest = Authorized(
                HttpMethod.Get, GitLabUrls.PipelineJobs(context.ApiUrl, context.ProjectPath, pipelineId, page), context);

            var jobsResponse = await http.SendAsync(jobsRequest, ct).ConfigureAwait(false);
            if (!jobsResponse.IsSuccessStatusCode)
                return (null, $"GitLab returned {(int)jobsResponse.StatusCode} listing the jobs of pipeline {pipelineId}.");

            var batch = await jobsResponse.Content
                .ReadFromJsonAsync<List<JobDto>>(cancellationToken: ct)
                .ConfigureAwait(false);
            if (batch is { Count: > 0 }) jobs.AddRange(batch);

            var next = jobsResponse.Headers.TryGetValues("X-Next-Page", out var values) ? values.FirstOrDefault() : null;
            if (string.IsNullOrEmpty(next)) break;
        }

        // Highest id among same-named jobs: a retried job keeps its name, and the newest attempt is
        // the one whose state describes this pipeline now.
        var match = jobs
            .Where(j => string.Equals(j.Name, jobName, StringComparison.Ordinal))
            .OrderByDescending(j => j.Id)
            .FirstOrDefault();

        if (match is not null) return (match, string.Empty);

        // The names are in the message on purpose: a job that does not exist is almost always a
        // spelling, and this is the only place the operator can see what the pipeline actually has.
        var available = jobs.Select(j => j.Name).Where(n => !string.IsNullOrEmpty(n)).Distinct(StringComparer.Ordinal).Take(20).ToList();
        var names = available.Count == 0 ? "it has none" : $"it has: {string.Join(", ", available)}";

        return (null, $"Pipeline {pipelineId} of merge request !{context.MergeRequestExternalId} has no job named '{jobName}' - {names}.");
    }

    private static DeployResult Failed(string message) => new(DeployOutcome.Failed, Message: message);

    private static string Ref(long id) => id.ToString(CultureInfo.InvariantCulture);

    /// <summary>The setting's value, or null when it is absent or blank.</summary>
    private static string? Setting(DeployContext context, string key) =>
        context.Settings.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;

    private static bool IsSuccess(string status) => status is "success";

    /// <summary>Waiting on a human or a schedule: the states GitLab lets you <c>play</c>.</summary>
    private static bool IsPlayable(string status) => status is "manual" or "scheduled";

    /// <summary>Already on its way to a verdict, so it is adopted rather than started again.</summary>
    private static bool IsInFlight(string status) =>
        status is "created" or "pending" or "preparing" or "waiting_for_resource" or "running" or "canceling";

    private static HttpRequestMessage Authorized(HttpMethod method, Uri url, DeployContext context)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add("PRIVATE-TOKEN", context.AccessToken);
        return request;
    }

    private sealed record PipelineDto(
        [property: JsonPropertyName("id")] long Id);

    private sealed record JobDto(
        [property: JsonPropertyName("id")] long Id,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("status")] string? Status);
}
