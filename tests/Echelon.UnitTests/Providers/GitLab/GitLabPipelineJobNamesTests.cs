using System.Net;
using Echelon.Providers.Abstractions.Deploy;
using Echelon.Providers.Abstractions.Vcs;
using Echelon.Providers.GitLab;
using Xunit;

namespace Echelon.UnitTests.Providers.GitLab;

/// <summary>
/// The job names offered by the deploy-target form's picker.
/// </summary>
/// <remarks>
/// The picker exists so nobody has to recall how a job is spelled, which only works if the list is
/// the pipeline's own vocabulary. The cases below are the three ways a naive implementation makes it
/// lie: reading one pipeline (a branch pipeline and a merge-request pipeline do not run the same
/// jobs), letting one unreadable pipeline empty the whole answer, and reporting a repository with no
/// CI as an error rather than as nothing to offer.
/// </remarks>
public class GitLabPipelineJobNamesTests
{
    private static readonly Uri ApiUrl = new("https://gitlab.example.com");
    private static CancellationToken Ct => CancellationToken.None;

    [Fact]
    public async Task UnionsTheJobsOfTheRecentPipelinesAndSortsThem()
    {
        // Deploy jobs that only exist on merge-request pipelines are exactly what this screen is
        // configuring, so answering from the newest pipeline alone would miss them about half the time.
        var router = new Router()
            .OnGet("/pipelines?per_page", """[ { "id": 99 }, { "id": 98 } ]""")
            .OnGet("/pipelines/99/jobs", """[ { "name": "build" }, { "name": "test" } ]""")
            .OnGet("/pipelines/98/jobs", """[ { "name": "build" }, { "name": "deploy:staging" } ]""");

        var names = await Source(router).ListRecentJobNamesAsync("group/api", 100, Ct);

        Assert.Equal(["build", "deploy:staging", "test"], names);
    }

    [Fact]
    public async Task OneUnreadablePipelineDoesNotEmptyTheAnswer()
    {
        // An older pipeline can be expired or deleted while the newest reads fine.
        var router = new Router()
            .OnGet("/pipelines?per_page", """[ { "id": 99 }, { "id": 98 } ]""")
            .OnGet("/pipelines/99/jobs", """[ { "name": "deploy:staging" } ]""")
            .OnGetFailing("/pipelines/98/jobs", HttpStatusCode.NotFound);

        var names = await Source(router).ListRecentJobNamesAsync("group/api", 100, Ct);

        Assert.Equal(["deploy:staging"], names);
    }

    [Fact]
    public async Task ARepositoryWithNoPipelineOffersNothing()
    {
        var router = new Router().OnGet("/pipelines?per_page", "[]");

        Assert.Empty(await Source(router).ListRecentJobNamesAsync("group/api", 100, Ct));
    }

    [Fact]
    public async Task HonoursTheLimit()
    {
        var router = new Router()
            .OnGet("/pipelines?per_page", """[ { "id": 99 } ]""")
            .OnGet("/pipelines/99/jobs", """[ { "name": "a" }, { "name": "b" }, { "name": "c" } ]""");

        Assert.Equal(2, (await Source(router).ListRecentJobNamesAsync("group/api", 2, Ct)).Count);
    }

    [Fact]
    public async Task AddressesTheProjectByItsEncodedPath()
    {
        var router = new Router().OnGet("/pipelines?per_page", "[]");

        await Source(router).ListRecentJobNamesAsync("group/sub/api", 100, Ct);

        Assert.Contains("group%2Fsub%2Fapi", Assert.Single(router.Requests).RequestUri!.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void TheJobStrategyDeclaresTheNeutralKeyThePickerLooksFor()
    {
        // The picker is offered to whichever strategy declares PipelineJobSettings.JobKey, so this
        // spelling is what connects the two. Rename it on one side only and the field silently loses
        // its picker - no compiler anywhere sees that.
        var schema = new GitLabJobStrategy(new HttpClient(new Router())).SettingsSchema;

        Assert.Contains(schema, s => s.Key == PipelineJobSettings.JobKey);
    }

    private static IPipelineJobSource Source(Router router) =>
        new GitLabProvider(
            new HttpClient(router),
            new VcsProviderContext("gitlab", ApiUrl, "secret-token"),
            VcsCapabilities.None);
}
