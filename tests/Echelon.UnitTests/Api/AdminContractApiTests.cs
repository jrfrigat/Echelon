using System.Net;
using System.Net.Http.Json;
using Echelon.Application.DTOs;
using Echelon.Core.Enums;
using Echelon.Providers.Abstractions;
using Echelon.Pwa.Services.Api;
using Xunit;

namespace Echelon.UnitTests.Api;

/// <summary>
/// The admin API answered over the wire, read back with the client's own settings.
/// </summary>
/// <remarks>
/// The controllers and the admin client share their response types now, so a shape cannot drift. What
/// can still drift is how the two ends write and read them, and what a request body looks like once an
/// enum is involved: the client serializes with <see cref="ApiClient.Json"/>, so a mode travels as
/// "AllOf" and not as 0. These tests run the real host and use those very settings, which is the only
/// way to see that end to end.
/// </remarks>
[Collection(ApiCollection.Name)]
public class AdminContractApiTests : IAsyncLifetime
{
    private ApiFactory _factory = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _factory = new ApiFactory();
        _client = _factory.CreateClient();
        await _factory.InitializeDatabaseAsync();
    }

    public Task DisposeAsync()
    {
        _client.Dispose();
        _factory.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task AReadinessRuleSurvivesTheRoundTrip()
    {
        // Written exactly as the browser writes it: the mode is an enum on both ends and travels as
        // its name, which is what the endpoint parses.
        var create = await _client.PostAsJsonAsync(
            "api/readiness-rules",
            new { Name = "prod gate", Mode = ReadyRule.AnyOf, RequiredSignals = new[] { "label:ready-for-prod" } },
            ApiClient.Json);

        Assert.Equal(HttpStatusCode.OK, create.StatusCode);

        var rules = await _client.GetFromJsonAsync<List<ReadinessRuleDto>>("api/readiness-rules", ApiClient.Json);

        var rule = Assert.Single(rules!);
        Assert.Equal("prod gate", rule.Name);
        Assert.Equal(ReadyRule.AnyOf, rule.Mode);
        Assert.Equal(["label:ready-for-prod"], rule.RequiredSignals);
    }

    [Fact]
    public async Task AnEnvironmentSurvivesTheRoundTrip()
    {
        var create = await _client.PostAsJsonAsync(
            "api/environments",
            new { Key = "staging", Name = "Staging", Order = 1, IsEnabled = true },
            ApiClient.Json);

        Assert.Equal(HttpStatusCode.OK, create.StatusCode);

        var environments = await _client.GetFromJsonAsync<List<EnvironmentDto>>("api/environments", ApiClient.Json);

        var environment = Assert.Single(environments!);
        Assert.Equal("staging", environment.Key);
        Assert.Equal(1, environment.Order);
        Assert.True(environment.IsEnabled);
        Assert.Null(environment.ReadinessRuleId);
    }

    [Fact]
    public async Task APagedListAnswersInTheShapeTheGridReads()
    {
        // Total, Page, PageSize and Items - the four the grid needs to size its pager. They used to be
        // an anonymous object here and a hand-written record in the browser.
        var page = await _client.GetFromJsonAsync<PagedResult<RepositoryDto>>(
            "api/repositories?page=1&pageSize=25", ApiClient.Json);

        Assert.NotNull(page);
        Assert.Equal(1, page.Page);
        Assert.Equal(25, page.PageSize);
        Assert.Equal(0, page.Total);
        Assert.Empty(page.Items);
    }

    [Fact]
    public async Task ProvidersAnswerWithTheirOwnSettingsSchema()
    {
        var providers = await _client.GetFromJsonAsync<List<ProviderTypeDto>>("api/providers/trackers", ApiClient.Json);

        Assert.NotNull(providers);
        var poll = Assert.Single(providers, p => p.ProviderType.EndsWith("-poll", StringComparison.Ordinal));

        // The whole schema, not a projection of it: a bounded interval only renders as a number field
        // if Kind, Min and Max survive the trip.
        Assert.Equal(IngestionMode.Poll, poll.Ingestion);
        Assert.Contains(poll.Settings, s => s.Kind == ProviderSettingKind.Int && s.Min is not null);
    }
}
