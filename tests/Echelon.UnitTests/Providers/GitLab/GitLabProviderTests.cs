using System.Net;
using System.Text;
using Echelon.Core.Enums;
using Echelon.Providers.Abstractions.Vcs;
using Echelon.Providers.GitLab;
using Xunit;

namespace Echelon.UnitTests.Providers.GitLab;

/// <summary>
/// What the adapter makes of GitLab's wire format.
/// </summary>
/// <remarks>
/// The date cases are the reason this file exists. GitLab stamps an offset
/// (<c>2026-07-17T10:00:00.000+03:00</c>), and binding that to a <see cref="DateTime"/> yields
/// <see cref="DateTimeKind.Local"/> - which SQL Server stores without complaint at the wrong instant
/// whenever the host is not on UTC, and which Npgsql refuses outright because it maps
/// <see cref="DateTime"/> to <c>timestamptz</c> and writes only <c>Kind=Utc</c>. So the defect is
/// invisible until the second database runs, and no existing test could have seen it: the adapter
/// was unreachable from here until <c>InternalsVisibleTo</c> was added for exactly this.
/// </remarks>
public class GitLabProviderTests
{
    private static readonly Uri ApiUrl = new("https://gitlab.example.com");
    private static CancellationToken Ct => CancellationToken.None;

    [Fact]
    public async Task ReadsAnOffsetTimestampAsTheSameInstantInUtc()
    {
        // 10:00+03:00 is 07:00Z. A DateTime bind would have produced the machine's local rendering
        // of that instant with Kind=Local; the assertion below pins both the kind and the value.
        var provider = ProviderReturning("""
            {
              "iid": 7, "source_branch": "feature/PROJ-1", "target_branch": "main",
              "state": "merged", "title": "PROJ-1: thing",
              "created_at": "2026-07-17T10:00:00.000+03:00",
              "merged_at": "2026-07-18T15:30:00.000+03:00",
              "labels": []
            }
            """);

        var mr = await provider.GetMergeRequestAsync("group/api", "7", Ct);

        Assert.NotNull(mr);
        Assert.Equal(DateTimeKind.Utc, mr!.CreatedAt.Kind);
        Assert.Equal(new DateTime(2026, 7, 17, 7, 0, 0, DateTimeKind.Utc), mr.CreatedAt);
        Assert.Equal(DateTimeKind.Utc, mr.MergedAt!.Value.Kind);
        Assert.Equal(new DateTime(2026, 7, 18, 12, 30, 0, DateTimeKind.Utc), mr.MergedAt);
    }

    [Fact]
    public async Task AZuluTimestampStaysTheSameInstant()
    {
        var provider = ProviderReturning("""
            {
              "iid": 7, "source_branch": "b", "target_branch": "main", "state": "opened",
              "title": "t", "created_at": "2026-07-17T07:00:00.000Z", "labels": []
            }
            """);

        var mr = await provider.GetMergeRequestAsync("group/api", "7", Ct);

        Assert.Equal(DateTimeKind.Utc, mr!.CreatedAt.Kind);
        Assert.Equal(new DateTime(2026, 7, 17, 7, 0, 0, DateTimeKind.Utc), mr.CreatedAt);
    }

    [Fact]
    public async Task AnAbsentMergedAtStaysNull()
    {
        // An open merge request has no merge time; null must survive as null rather than becoming
        // a default DateTime, which archiving would read as "merged in year 1".
        var provider = ProviderReturning("""
            {
              "iid": 7, "source_branch": "b", "target_branch": "main", "state": "opened",
              "title": "t", "created_at": "2026-07-17T07:00:00.000Z", "labels": []
            }
            """);

        var mr = await provider.GetMergeRequestAsync("group/api", "7", Ct);

        Assert.Null(mr!.MergedAt);
    }

    [Fact]
    public async Task NormalizesTheStateAndCarriesLabelsAndPipeline()
    {
        var provider = ProviderReturning("""
            {
              "iid": 7, "source_branch": "b", "target_branch": "main", "state": "merged",
              "title": "t", "created_at": "2026-07-17T07:00:00.000Z",
              "labels": ["ready-for-prod", "backend"],
              "head_pipeline": { "status": "success" }
            }
            """);

        var mr = await provider.GetMergeRequestAsync("group/api", "7", Ct);

        Assert.Equal(MergeRequestStatus.Merged, mr!.Status);
        Assert.Equal(["ready-for-prod", "backend"], mr.Labels);
        Assert.Equal("success", mr.PipelineStatus);
    }

    [Fact]
    public async Task AMergeRequestTheServerDoesNotHaveIsNullRatherThanAnError()
    {
        var provider = ProviderReturning("{}", HttpStatusCode.NotFound);

        Assert.Null(await provider.GetMergeRequestAsync("group/api", "7", Ct));
    }

    [Fact]
    public async Task SendsTheTokenPerRequestRatherThanOnTheClient()
    {
        // The HttpClient comes from the shared factory pool and may serve other connections, so a
        // header set on the client would leak one connection's token onto another's request.
        var handler = new StubHandler("""
            { "iid": 7, "source_branch": "b", "target_branch": "main", "state": "opened",
              "title": "t", "created_at": "2026-07-17T07:00:00.000Z", "labels": [] }
            """);
        var provider = Provider(handler);

        await provider.GetMergeRequestAsync("group/api", "7", Ct);

        Assert.Equal("secret-token", Assert.Single(handler.Requests).Headers.GetValues("PRIVATE-TOKEN").Single());
        Assert.Null(handler.ClientDefaultToken);
    }

    [Fact]
    public async Task AddressesTheProjectByItsEncodedPath()
    {
        // A subgroup path must survive as %2F; interpolating it raw is how a valid project 404s.
        var handler = new StubHandler("""
            { "iid": 7, "source_branch": "b", "target_branch": "main", "state": "opened",
              "title": "t", "created_at": "2026-07-17T07:00:00.000Z", "labels": [] }
            """);

        await Provider(handler).GetMergeRequestAsync("group/sub/api", "7", Ct);

        Assert.Contains("group%2Fsub%2Fapi", Assert.Single(handler.Requests).RequestUri!.ToString(), StringComparison.Ordinal);
    }

    private static IVcsProvider ProviderReturning(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        Provider(new StubHandler(json, status));

    private static IVcsProvider Provider(StubHandler handler)
    {
        var http = new HttpClient(handler);
        handler.ClientDefaultToken = http.DefaultRequestHeaders.TryGetValues("PRIVATE-TOKEN", out var v)
            ? v.FirstOrDefault()
            : null;

        return new GitLabProvider(
            http,
            new VcsProviderContext("gitlab", ApiUrl, "secret-token"),
            VcsCapabilities.None);
    }

    /// <summary>Answers every request with one canned body, recording what was asked.</summary>
    private sealed class StubHandler(string json, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        private readonly List<HttpRequestMessage> _requests = [];

        public IReadOnlyList<HttpRequestMessage> Requests => _requests;

        /// <summary>Whatever the client itself carried, to prove the token is not set there.</summary>
        public string? ClientDefaultToken { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _requests.Add(request);

            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        }
    }
}
