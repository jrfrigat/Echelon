using System.Net;
using System.Text;

namespace Echelon.UnitTests.Providers.GitLab;

/// <summary>
/// A GitLab stub that answers by URL, and records what was asked.
/// </summary>
/// <remarks>
/// The one-response stub next door cannot express what these cases are about: running a job means
/// three or four different calls in sequence - the merge request's pipelines, that pipeline's jobs,
/// then play or retry - and it is the routing between them that the tests are asserting. Matching is
/// a plain substring of the URL, in registration order, so a rule can be as specific as
/// <c>/jobs/12/play</c> or as loose as <c>&amp;page=2</c>.
/// </remarks>
internal sealed class Router : HttpMessageHandler
{
    private readonly List<(HttpMethod Method, string UrlContains, string Json, string? NextPage)> _routes = [];

    /// <summary>Every request that reached the stub, in order.</summary>
    public List<HttpRequestMessage> Requests { get; } = [];

    /// <summary>Answers a GET whose URL contains <paramref name="urlContains"/>.</summary>
    /// <param name="urlContains">Substring of the URL to match.</param>
    /// <param name="json">The body to answer with.</param>
    /// <param name="nextPage">Sets <c>X-Next-Page</c>, so paging can be exercised.</param>
    public Router OnGet(string urlContains, string json, string? nextPage = null)
    {
        _routes.Add((HttpMethod.Get, urlContains, json, nextPage));
        return this;
    }

    /// <summary>Answers a POST whose URL contains <paramref name="urlContains"/>.</summary>
    /// <param name="urlContains">Substring of the URL to match.</param>
    /// <param name="json">The body to answer with.</param>
    public Router OnPost(string urlContains, string json)
    {
        _routes.Add((HttpMethod.Post, urlContains, json, null));
        return this;
    }

    /// <summary>Answers anything matching <paramref name="urlContains"/> with a failure status.</summary>
    /// <param name="urlContains">Substring of the URL to match.</param>
    /// <param name="status">The status to answer with.</param>
    public Router OnGetFailing(string urlContains, HttpStatusCode status)
    {
        _routes.Add((HttpMethod.Get, urlContains, StatusMarker + (int)status, null));
        return this;
    }

    private const string StatusMarker = "!status:";

    /// <inheritdoc/>
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);

        var url = request.RequestUri!.ToString();
        foreach (var route in _routes)
        {
            if (route.Method != request.Method || !url.Contains(route.UrlContains, StringComparison.Ordinal)) continue;

            if (route.Json.StartsWith(StatusMarker, StringComparison.Ordinal))
            {
                var status = int.Parse(route.Json[StatusMarker.Length..], System.Globalization.CultureInfo.InvariantCulture);
                return Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)
                {
                    Content = new StringContent("{}", Encoding.UTF8, "application/json")
                });
            }

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(route.Json, Encoding.UTF8, "application/json")
            };
            if (route.NextPage is not null) response.Headers.Add("X-Next-Page", route.NextPage);

            return Task.FromResult(response);
        }

        // Nothing matched: a 404 with a body, which is what GitLab answers for a path that is not there.
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json")
        });
    }
}
