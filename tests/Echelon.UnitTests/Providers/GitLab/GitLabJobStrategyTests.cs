using Echelon.Providers.Abstractions.Deploy;
using Echelon.Providers.GitLab;
using Xunit;

namespace Echelon.UnitTests.Providers.GitLab;

/// <summary>
/// Running one job of the merge request's own pipeline.
/// </summary>
/// <remarks>
/// The verb is the whole point of these tests. GitLab plays a job that is waiting on its manual gate
/// and retries one that has already run, and using the wrong one is not a no-op - it answers
/// "Unplayable Job" and the deploy fails for a reason that says nothing about the deploy. Nothing
/// else in the suite can catch that: no GitLab is reachable from here, so the wire contract is only
/// ever asserted against a stub.
/// </remarks>
public class GitLabJobStrategyTests
{
    private const string JobName = "deploy:staging";
    private static CancellationToken Ct => CancellationToken.None;

    [Fact]
    public async Task PlaysAManualJobOfTheNewestPipeline()
    {
        // Ids deliberately out of order: the newest pipeline is the highest id, not the first row.
        var router = new Router()
            .OnGet("/merge_requests/7/pipelines", """[ { "id": 98 }, { "id": 99 } ]""")
            .OnGet("/pipelines/99/jobs", $$"""[ { "id": 12, "name": "{{JobName}}", "status": "manual" } ]""")
            .OnPost("/jobs/12/play", """{ "id": 12, "status": "pending" }""");

        var result = await Strategy(router).StartAsync(Context((GitLabJobStrategy.JobKey, JobName)), Ct);

        Assert.Equal(DeployOutcome.Awaiting, result.Outcome);
        Assert.Equal("12", result.ExternalRef);
        Assert.Contains(router.Requests, r =>
            r.Method == HttpMethod.Post && r.RequestUri!.AbsolutePath.EndsWith("/jobs/12/play", StringComparison.Ordinal));
        Assert.All(router.Requests, r => Assert.Equal("secret-token", r.Headers.GetValues("PRIVATE-TOKEN").Single()));
    }

    [Fact]
    public async Task PipelineSourcePicksTheKindOfPipelineRatherThanTheNewestOne()
    {
        // A merge request usually carries several pipelines - the branch push built one, the merge
        // request built another - and both can hold a job of the same name. Newest-wins would deploy
        // from whichever happened to run last, which is not a decision anybody made.
        var router = TwoPipelines(
            newer: """{ "id": 101, "source": "push", "status": "success", "sha": "abc123def456" }""",
            older: """{ "id": 100, "source": "merge_request_event", "status": "success", "sha": "abc123def456" }""",
            newerJobs: $$"""[ { "id": 30, "name": "{{JobName}}", "status": "manual" } ]""",
            olderJobs: $$"""[ { "id": 12, "name": "{{JobName}}", "status": "manual" } ]""");

        var result = await Strategy(router).StartAsync(
            Context((GitLabJobStrategy.JobKey, JobName),
                    (GitLabJobStrategy.PipelineSourceKey, "merge_request_event")), Ct);

        Assert.Equal(DeployOutcome.Awaiting, result.Outcome);
        Assert.Equal("12", result.ExternalRef);
    }

    [Fact]
    public async Task PipelineStatusSkipsAPipelineThatIsNotInThatState()
    {
        // "Deploy only from a build that passed" is the reason this filter exists.
        var router = TwoPipelines(
            newer: """{ "id": 101, "source": "push", "status": "failed", "sha": "abc123def456" }""",
            older: """{ "id": 100, "source": "push", "status": "success", "sha": "abc123def456" }""",
            newerJobs: $$"""[ { "id": 30, "name": "{{JobName}}", "status": "manual" } ]""",
            olderJobs: $$"""[ { "id": 12, "name": "{{JobName}}", "status": "manual" } ]""");

        var result = await Strategy(router).StartAsync(
            Context((GitLabJobStrategy.JobKey, JobName),
                    (GitLabJobStrategy.PipelineStatusKey, "success")), Ct);

        Assert.Equal("12", result.ExternalRef);
    }

    [Fact]
    public async Task PipelinePickFirstTakesTheOldestMatchingPipeline()
    {
        var router = TwoPipelines(
            newer: """{ "id": 101, "source": "push", "status": "success", "sha": "abc123def456" }""",
            older: """{ "id": 100, "source": "push", "status": "success", "sha": "abc123def456" }""",
            newerJobs: $$"""[ { "id": 30, "name": "{{JobName}}", "status": "manual" } ]""",
            olderJobs: $$"""[ { "id": 12, "name": "{{JobName}}", "status": "manual" } ]""");

        var result = await Strategy(router).StartAsync(
            Context((GitLabJobStrategy.JobKey, JobName),
                    (GitLabJobStrategy.PipelinePickKey, GitLabJobStrategy.PickFirst)), Ct);

        Assert.Equal("12", result.ExternalRef);
    }

    [Fact]
    public async Task LooksInTheSiblingPipelineOfTheSameCommitWhenTheFirstDoesNotHoldTheJob()
    {
        // The common shape with no settings at all: the push pipeline built and tested, the
        // merge-request pipeline carries the deploy gate. Both are the same commit, so either is a
        // legitimate place to find the job.
        var router = TwoPipelines(
            newer: """{ "id": 101, "source": "push", "status": "success", "sha": "abc123def456" }""",
            older: """{ "id": 100, "source": "merge_request_event", "status": "manual", "sha": "abc123def456" }""",
            newerJobs: """[ { "id": 30, "name": "build", "status": "success" } ]""",
            olderJobs: $$"""[ { "id": 12, "name": "{{JobName}}", "status": "manual" } ]""");

        var result = await Strategy(router).StartAsync(Context((GitLabJobStrategy.JobKey, JobName)), Ct);

        Assert.Equal(DeployOutcome.Awaiting, result.Outcome);
        Assert.Equal("12", result.ExternalRef);
    }

    [Fact]
    public async Task NeverFallsThroughToAnEarlierCommitsPipeline()
    {
        // The safety property. Scanning on past the chosen commit would deploy something the operator
        // did not pick - an older commit whose pipeline happens to still have the job - and it would
        // look like a success. Failing is the right answer.
        var router = TwoPipelines(
            newer: """{ "id": 101, "source": "push", "status": "success", "sha": "abc123def456" }""",
            older: """{ "id": 100, "source": "push", "status": "success", "sha": "0ldc0mmit0000" }""",
            newerJobs: """[ { "id": 30, "name": "build", "status": "success" } ]""",
            olderJobs: $$"""[ { "id": 12, "name": "{{JobName}}", "status": "manual" } ]""");

        var result = await Strategy(router).StartAsync(Context((GitLabJobStrategy.JobKey, JobName)), Ct);

        Assert.Equal(DeployOutcome.Failed, result.Outcome);
        Assert.Contains("abc123de", result.Message!, StringComparison.Ordinal);
        Assert.DoesNotContain(router.Requests, r => r.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task FiltersThatMatchNothingAreToldWhatTheMergeRequestHas()
    {
        var router = new Router()
            .OnGet("/merge_requests/7/pipelines",
                """[ { "id": 101, "source": "push", "status": "failed", "sha": "abc123def456" } ]""");

        var result = await Strategy(router).StartAsync(
            Context((GitLabJobStrategy.JobKey, JobName),
                    (GitLabJobStrategy.PipelineStatusKey, "success")), Ct);

        Assert.Equal(DeployOutcome.Failed, result.Outcome);
        Assert.Contains("101 (push/failed)", result.Message!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AJobThatAlreadySucceededIsAlreadyDoneAndNothingIsStarted()
    {
        // The idempotency obligation in IDeployStrategy: a redelivered step must not deploy twice.
        var router = Pipeline(status: "success");

        var result = await Strategy(router).StartAsync(Context((GitLabJobStrategy.JobKey, JobName)), Ct);

        Assert.Equal(DeployOutcome.AlreadyDone, result.Outcome);
        Assert.DoesNotContain(router.Requests, r => r.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task RerunAlwaysRetriesASuccessfulJobAndAdoptsTheNewId()
    {
        // A retry is a NEW job with a new id. Polling the old one would watch a job that will never
        // move again and time the rollout out on a deploy that actually ran.
        var router = Pipeline(status: "success")
            .OnPost("/jobs/12/retry", """{ "id": 13, "status": "pending" }""");

        var result = await Strategy(router).StartAsync(
            Context((GitLabJobStrategy.JobKey, JobName), (GitLabJobStrategy.RerunKey, GitLabJobStrategy.RerunAlways)), Ct);

        Assert.Equal(DeployOutcome.Awaiting, result.Outcome);
        Assert.Equal("13", result.ExternalRef);
    }

    [Fact]
    public async Task AFailedJobIsRetriedRatherThanPlayed()
    {
        var router = Pipeline(status: "failed")
            .OnPost("/jobs/12/retry", """{ "id": 14, "status": "pending" }""");

        var result = await Strategy(router).StartAsync(Context((GitLabJobStrategy.JobKey, JobName)), Ct);

        Assert.Equal(DeployOutcome.Awaiting, result.Outcome);
        Assert.Equal("14", result.ExternalRef);
        Assert.DoesNotContain(router.Requests, r => r.RequestUri!.AbsolutePath.EndsWith("/play", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ARunningJobIsAdoptedRatherThanStartedAgain()
    {
        var router = Pipeline(status: "running");

        var result = await Strategy(router).StartAsync(Context((GitLabJobStrategy.JobKey, JobName)), Ct);

        Assert.Equal(DeployOutcome.Awaiting, result.Outcome);
        Assert.Equal("12", result.ExternalRef);
        Assert.DoesNotContain(router.Requests, r => r.Method == HttpMethod.Post);
    }

    [Fact]
    public async Task WithoutAJobNameTheDeployFailsNamingTheSetting()
    {
        var result = await Strategy(new Router()).StartAsync(Context(), Ct);

        Assert.Equal(DeployOutcome.Failed, result.Outcome);
        Assert.Contains(GitLabJobStrategy.JobKey, result.Message!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMergeRequestWithNoPipelineIsToldSo()
    {
        var router = new Router().OnGet("/merge_requests/7/pipelines", "[]");

        var result = await Strategy(router).StartAsync(Context((GitLabJobStrategy.JobKey, JobName)), Ct);

        Assert.Equal(DeployOutcome.Failed, result.Outcome);
        Assert.Contains("no pipeline", result.Message!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AMisspelledJobIsToldWhatThePipelineActuallyHas()
    {
        // The only place an operator can see the truth: a job that does not exist is nearly always a
        // spelling, and a bare "not found" sends them to the CI file to guess.
        var router = new Router()
            .OnGet("/merge_requests/7/pipelines", """[ { "id": 99 } ]""")
            .OnGet("/pipelines/99/jobs", """
                [ { "id": 10, "name": "build", "status": "success" },
                  { "id": 12, "name": "deploy:staging", "status": "manual" } ]
                """);

        var result = await Strategy(router).StartAsync(Context((GitLabJobStrategy.JobKey, "deploy")), Ct);

        Assert.Equal(DeployOutcome.Failed, result.Outcome);
        Assert.Contains("deploy:staging", result.Message!, StringComparison.Ordinal);
        Assert.Contains("build", result.Message!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task FindsAJobBeyondTheFirstPageOfJobs()
    {
        // A monorepo pipeline runs more than a hundred jobs, and GitLab caps per_page at 100. Reading
        // only the first page would report the deploy job missing on exactly the repositories that
        // need it most.
        var router = new Router()
            .OnGet("/merge_requests/7/pipelines", """[ { "id": 99 } ]""")
            .OnGet("&page=1", """[ { "id": 1, "name": "build", "status": "success" } ]""", nextPage: "2")
            .OnGet("&page=2", $$"""[ { "id": 12, "name": "{{JobName}}", "status": "manual" } ]""")
            .OnPost("/jobs/12/play", """{ "id": 12, "status": "pending" }""");

        var result = await Strategy(router).StartAsync(Context((GitLabJobStrategy.JobKey, JobName)), Ct);

        Assert.Equal(DeployOutcome.Awaiting, result.Outcome);
        Assert.Equal("12", result.ExternalRef);
    }

    [Fact]
    public async Task TakesTheNewestAttemptOfARetriedJob()
    {
        // A retried job keeps its name, so the same name can appear twice; the newest attempt is the
        // one whose status describes the pipeline now.
        var router = new Router()
            .OnGet("/merge_requests/7/pipelines", """[ { "id": 99 } ]""")
            .OnGet("/pipelines/99/jobs", $$"""
                [ { "id": 12, "name": "{{JobName}}", "status": "failed" },
                  { "id": 20, "name": "{{JobName}}", "status": "success" } ]
                """);

        var result = await Strategy(router).StartAsync(Context((GitLabJobStrategy.JobKey, JobName)), Ct);

        Assert.Equal(DeployOutcome.AlreadyDone, result.Outcome);
        Assert.Equal("20", result.ExternalRef);
    }

    [Theory]
    [InlineData("success", DeployOutcome.Succeeded)]
    [InlineData("failed", DeployOutcome.Failed)]
    [InlineData("canceled", DeployOutcome.Failed)]
    [InlineData("running", DeployOutcome.Awaiting)]
    [InlineData("pending", DeployOutcome.Awaiting)]
    // A job back on its manual gate was never started: waiting for a press nobody will make is worse
    // than saying so.
    [InlineData("manual", DeployOutcome.Failed)]
    public async Task PollMapsTheJobStatus(string status, DeployOutcome expected)
    {
        var router = new Router().OnGet("/jobs/12", $$"""{ "id": 12, "name": "{{JobName}}", "status": "{{status}}" }""");

        var result = await Strategy(router).PollAsync(Context((GitLabJobStrategy.JobKey, JobName)), "12", Ct);

        Assert.Equal(expected, result.Outcome);
    }

    [Fact]
    public async Task ReconcileReattachesToTheRunningJobRatherThanStartingASecond()
    {
        // What the pipeline strategy cannot do: a job is addressed by name inside the merge request's
        // own pipeline, so a step resumed after a crash re-finds exactly the run it started.
        var result = await Strategy(Pipeline(status: "running"))
            .ReconcileAsync(Context((GitLabJobStrategy.JobKey, JobName)), Ct);

        Assert.NotNull(result);
        Assert.Equal(DeployOutcome.Awaiting, result.Outcome);
        Assert.Equal("12", result.ExternalRef);
    }

    [Fact]
    public async Task ReconcileReportsAFinishedJobAsAlreadyDone()
    {
        var result = await Strategy(Pipeline(status: "success"))
            .ReconcileAsync(Context((GitLabJobStrategy.JobKey, JobName)), Ct);

        Assert.NotNull(result);
        Assert.Equal(DeployOutcome.AlreadyDone, result.Outcome);
    }

    [Fact]
    public async Task ReconcileFindsNothingWhenTheJobHasNotRun()
    {
        // Null means "start fresh", which is right for a job still waiting on its gate.
        Assert.Null(await Strategy(Pipeline(status: "manual"))
            .ReconcileAsync(Context((GitLabJobStrategy.JobKey, JobName)), Ct));
    }

    [Fact]
    public async Task AddressesTheProjectByItsEncodedPath()
    {
        // A subgroup path must survive as %2F; interpolating it raw is how a valid project 404s.
        var router = new Router().OnGet("/pipelines", "[]");

        await Strategy(router).StartAsync(
            ContextFor("group/sub/api", (GitLabJobStrategy.JobKey, JobName)), Ct);

        Assert.Contains("group%2Fsub%2Fapi", Assert.Single(router.Requests).RequestUri!.ToString(), StringComparison.Ordinal);
    }

    /// <summary>Two pipelines on one merge request, each with its own jobs - the selection cases.</summary>
    private static Router TwoPipelines(string newer, string older, string newerJobs, string olderJobs) =>
        new Router()
            .OnGet("/merge_requests/7/pipelines", $"[ {newer}, {older} ]")
            .OnGet("/pipelines/101/jobs", newerJobs)
            .OnGet("/pipelines/100/jobs", olderJobs)
            .OnPost("/jobs/12/play", """{ "id": 12, "status": "pending" }""")
            .OnPost("/jobs/30/play", """{ "id": 30, "status": "pending" }""");

    /// <summary>One pipeline holding one job in the given state - the shape most cases need.</summary>
    private static Router Pipeline(string status) =>
        new Router()
            .OnGet("/merge_requests/7/pipelines", """[ { "id": 99 } ]""")
            .OnGet("/pipelines/99/jobs", $$"""[ { "id": 12, "name": "{{JobName}}", "status": "{{status}}" } ]""");

    private static GitLabJobStrategy Strategy(Router router) => new(new HttpClient(router));

    private static DeployContext Context(params (string Key, string Value)[] settings) =>
        ContextFor("group/api", settings);

    private static DeployContext ContextFor(string projectPath, params (string Key, string Value)[] settings) =>
        new(new Uri("https://gitlab.example.com"),
            "secret-token",
            "gitlab-main",
            projectPath,
            "7",
            "staging",
            settings.ToDictionary(s => s.Key, s => s.Value));

}
