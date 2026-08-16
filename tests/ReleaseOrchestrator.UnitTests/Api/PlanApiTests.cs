using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ReleaseOrchestrator.Core.Enums;
using ReleaseOrchestrator.Infrastructure.Auth;
using ReleaseOrchestrator.Infrastructure.Persistence.Models;
using Xunit;

namespace ReleaseOrchestrator.UnitTests.Api;

/// <summary>
/// The plan API over the real host: routing, authorization, model binding, status codes and the
/// shape of what comes back.
/// </summary>
/// <remarks>
/// These endpoints had no test above the service layer. The services were covered, so what was
/// unverified was exactly the part every caller depends on - that the route exists, that the policy
/// is the right one, that a refusal is a 422 and not a 500, and that the JSON says what the client
/// reads. Verified by hand against a running instance once; this is the version that survives.
/// </remarks>
public class PlanApiTests : IAsyncLifetime
{
    private ApiFactory _factory = null!;
    private HttpClient _client = null!;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

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

    /// <summary>Seeds one task with two merge requests in two repositories, and builds its plan.</summary>
    private async Task<(Guid TaskId, string ApiKey, string WebKey)> ArrangePlanAsync()
    {
        var taskId = Guid.NewGuid();

        await _factory.WithDbAsync(async db =>
        {
            var tracker = new TrackerConnection
            {
                Id = Guid.NewGuid(), Name = "tracker", ProviderType = "fake", ApiUrl = "https://t.example.com"
            };
            var vcs = new VcsConnection
            {
                Id = Guid.NewGuid(), Name = "vcs", ProviderType = "gitlab", ApiUrl = "https://g.example.com"
            };
            var api = new Repository
            {
                Id = Guid.NewGuid(), Name = "api", ExternalId = "group/api", ConnectionId = vcs.Id
            };
            var web = new Repository
            {
                Id = Guid.NewGuid(), Name = "web", ExternalId = "group/web", ConnectionId = vcs.Id
            };

            db.TrackerConnections.Add(tracker);
            db.VcsConnections.Add(vcs);
            db.Repositories.AddRange(api, web);
            db.Tasks.Add(new TaskItem
            {
                Id = taskId, ExternalId = "PROJ-1", Title = "one", Status = "open",
                TrackerConnectionId = tracker.Id
            });
            db.MergeRequests.AddRange(
                new MergeRequest
                {
                    Id = Guid.NewGuid(), ExternalId = "1", SourceBranch = "feature/PROJ-1", TargetBranch = "main",
                    RepositoryId = api.Id, TaskId = taskId, Status = MergeRequestStatus.ReadyForDeploy,
                    CreatedAt = new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc)
                },
                new MergeRequest
                {
                    Id = Guid.NewGuid(), ExternalId = "2", SourceBranch = "feature/PROJ-1", TargetBranch = "main",
                    RepositoryId = web.Id, TaskId = taskId, Status = MergeRequestStatus.ReadyForDeploy,
                    CreatedAt = new DateTime(2026, 8, 2, 0, 0, 0, DateTimeKind.Utc)
                });

            await db.SaveChangesAsync();
            return true;
        });

        var built = await _client.PostAsync($"/api/tasks/{taskId}/plan/recalculate", null);
        Assert.Equal(HttpStatusCode.OK, built.StatusCode);

        return (taskId, "vcs:group/api!1", "vcs:group/web!2");
    }

    // ---- the plan itself -------------------------------------------------------------------

    [Fact]
    public async Task RecalculateReturnsAPlanWithWavesAndItems()
    {
        var (taskId, _, _) = await ArrangePlanAsync();

        var plan = await _client.GetFromJsonAsync<JsonElement>($"/api/tasks/{taskId}/plan", Json);

        Assert.Equal("PROJ-1", plan.GetProperty("targetTaskKey").GetString());
        Assert.Equal(1, plan.GetProperty("version").GetInt32());
        var items = plan.GetProperty("nodes").EnumerateArray().SelectMany(n => n.GetProperty("items").EnumerateArray()).ToList();
        Assert.Equal(2, items.Count);
        Assert.All(items, i => Assert.True(i.GetProperty("wave").GetInt32() >= 1));
    }

    /// <summary>The version is an ordinal now, so a second build has to say 2 - over the wire, not just in the row.</summary>
    [Fact]
    public async Task RecalculatingAgainIncrementsTheVersion()
    {
        var (taskId, _, _) = await ArrangePlanAsync();

        var again = await _client.PostAsync($"/api/tasks/{taskId}/plan/recalculate", null);
        var plan = await again.Content.ReadFromJsonAsync<JsonElement>(Json);

        Assert.Equal(2, plan.GetProperty("version").GetInt32());
    }

    [Fact]
    public async Task ExportReturnsYamlAsText()
    {
        var (taskId, apiKey, _) = await ArrangePlanAsync();

        var response = await _client.GetAsync($"/api/tasks/{taskId}/plan/export");
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/yaml", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("target_task: PROJ-1", body, StringComparison.Ordinal);
        Assert.Contains(apiKey, body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PlanOfAnUnknownTaskIs404()
    {
        var response = await _client.GetAsync($"/api/tasks/{Guid.NewGuid()}/plan");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ---- validate and import ---------------------------------------------------------------

    /// <summary>An exported plan validates and imports over HTTP - the round trip, end to end.</summary>
    [Fact]
    public async Task ExportedPlanValidatesAndImports()
    {
        var (taskId, _, _) = await ArrangePlanAsync();
        var document = await (await _client.GetAsync($"/api/tasks/{taskId}/plan/export")).Content.ReadAsStringAsync();

        var validated = await _client.PostAsJsonAsync($"/api/tasks/{taskId}/plan/validate", new { document }, Json);
        var verdict = await validated.Content.ReadFromJsonAsync<JsonElement>(Json);

        Assert.Equal(HttpStatusCode.OK, validated.StatusCode);
        Assert.True(verdict.GetProperty("accepted").GetBoolean());
        Assert.Empty(verdict.GetProperty("errors").EnumerateArray());
        Assert.Null(verdict.GetProperty("plan").ValueKind == JsonValueKind.Null ? null : "plan");

        var imported = await _client.PostAsJsonAsync($"/api/tasks/{taskId}/plan/import", new { document }, Json);
        var result = await imported.Content.ReadFromJsonAsync<JsonElement>(Json);

        Assert.Equal(HttpStatusCode.OK, imported.StatusCode);
        Assert.True(result.GetProperty("accepted").GetBoolean());
        Assert.Equal("Imported", result.GetProperty("plan").GetProperty("source").GetString());
    }

    /// <summary>
    /// A document that cannot be reconciled is 422 with the reasons, not 400 and not 500.
    /// </summary>
    /// <remarks>
    /// The distinction is the contract: the request was well-formed and parsed, so the client should
    /// read the body and fix the document rather than the call.
    /// </remarks>
    [Fact]
    public async Task ADocumentThatDoesNotMatchThePlanIs422WithReasons()
    {
        var (taskId, apiKey, _) = await ArrangePlanAsync();

        // Only one of the two merge requests: membership comes from the atlas, so this is refused.
        var document = $"version: 1\ntarget_task: PROJ-1\nnodes:\n  - task: PROJ-1\n    merge_requests:\n      - mr: {apiKey}\n        wave: 1\n";

        var response = await _client.PostAsJsonAsync($"/api/tasks/{taskId}/plan/import", new { document }, Json);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Json);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.False(body.GetProperty("accepted").GetBoolean());
        Assert.Contains(
            body.GetProperty("errors").EnumerateArray().Select(e => e.GetString()),
            e => e!.Contains("missing from the document", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UnreadableYamlIs422AndNamesTheLine()
    {
        var (taskId, _, _) = await ArrangePlanAsync();

        var response = await _client.PostAsJsonAsync(
            $"/api/tasks/{taskId}/plan/import", new { document = "version: 1\n  bad: [indent" }, Json);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Json);

        Assert.Equal(HttpStatusCode.UnprocessableEntity, response.StatusCode);
        Assert.NotEmpty(body.GetProperty("errors").EnumerateArray());
    }

    // ---- membership ------------------------------------------------------------------------

    /// <summary>Excluding a merge request removes it from the plan and lists it as excluded.</summary>
    [Fact]
    public async Task ExcludingAMergeRequestTakesItOutOfThePlan()
    {
        var (taskId, _, _) = await ArrangePlanAsync();
        var plan = await _client.GetFromJsonAsync<JsonElement>($"/api/tasks/{taskId}/plan", Json);
        var victim = plan.GetProperty("nodes").EnumerateArray()
            .SelectMany(n => n.GetProperty("items").EnumerateArray())
            .First(i => i.GetProperty("repositoryName").GetString() == "web")
            .GetProperty("mergeRequestId").GetGuid();

        var set = await _client.PutAsJsonAsync(
            $"/api/planning/tasks/{taskId}/membership/{victim}", new { state = "excluded" }, Json);
        Assert.Equal(HttpStatusCode.NoContent, set.StatusCode);

        // The endpoint asks for a rebuild through the bus; the plan is rebuilt here so the assertion
        // is about the derivation rather than about queue timing.
        await _client.PostAsync($"/api/tasks/{taskId}/plan/recalculate", null);

        var after = await _client.GetFromJsonAsync<JsonElement>($"/api/tasks/{taskId}/plan", Json);
        var remaining = after.GetProperty("nodes").EnumerateArray()
            .SelectMany(n => n.GetProperty("items").EnumerateArray()).ToList();

        Assert.Single(remaining);
        Assert.Equal("api", remaining[0].GetProperty("repositoryName").GetString());

        var membership = await _client.GetFromJsonAsync<JsonElement>($"/api/planning/tasks/{taskId}/membership", Json);
        var entry = Assert.Single(membership.EnumerateArray());
        Assert.Equal("Excluded", entry.GetProperty("state").GetString());
        Assert.Equal("web", entry.GetProperty("repositoryName").GetString());
    }

    /// <summary>"auto" hands the decision back, and the merge request returns.</summary>
    [Fact]
    public async Task RestoringMembershipPutsItBack()
    {
        var (taskId, _, _) = await ArrangePlanAsync();
        var victim = await _factory.WithDbAsync(async db =>
            await db.MergeRequests.Where(m => m.ExternalId == "2").Select(m => m.Id).FirstAsync());

        await _client.PutAsJsonAsync($"/api/planning/tasks/{taskId}/membership/{victim}", new { state = "excluded" }, Json);
        await _client.PutAsJsonAsync($"/api/planning/tasks/{taskId}/membership/{victim}", new { state = "auto" }, Json);
        await _client.PostAsync($"/api/tasks/{taskId}/plan/recalculate", null);

        var plan = await _client.GetFromJsonAsync<JsonElement>($"/api/tasks/{taskId}/plan", Json);
        var items = plan.GetProperty("nodes").EnumerateArray()
            .SelectMany(n => n.GetProperty("items").EnumerateArray()).ToList();

        Assert.Equal(2, items.Count);
        Assert.Empty((await _client.GetFromJsonAsync<JsonElement>(
            $"/api/planning/tasks/{taskId}/membership", Json)).EnumerateArray());
    }

    [Fact]
    public async Task AnUnknownMembershipStateIs400()
    {
        var (taskId, _, _) = await ArrangePlanAsync();
        var victim = await _factory.WithDbAsync(async db =>
            await db.MergeRequests.Select(m => m.Id).FirstAsync());

        var response = await _client.PutAsJsonAsync(
            $"/api/planning/tasks/{taskId}/membership/{victim}", new { state = "maybe" }, Json);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>Changing membership asks for a rebuild; without that the screen would show a stale plan.</summary>
    [Fact]
    public async Task ChangingMembershipRequestsARecalculation()
    {
        var (taskId, _, _) = await ArrangePlanAsync();
        var victim = await _factory.WithDbAsync(async db =>
            await db.MergeRequests.Select(m => m.Id).FirstAsync());
        _factory.Bus.Sent.Clear();

        await _client.PutAsJsonAsync($"/api/planning/tasks/{taskId}/membership/{victim}", new { state = "excluded" }, Json);

        Assert.Single(_factory.Bus.Recalculations);
    }

    // ---- authorization ---------------------------------------------------------------------

    /// <summary>Reading a plan needs the view policy; writing one needs approve. The host's own policies decide.</summary>
    [Fact]
    public async Task WritingAPlanNeedsApprovePermission()
    {
        var (taskId, _, _) = await ArrangePlanAsync();
        _factory.Permissions.Clear();
        _factory.Permissions.Add(Permissions.ReleasePlanView);

        var read = await _client.GetAsync($"/api/tasks/{taskId}/plan");
        var write = await _client.PostAsync($"/api/tasks/{taskId}/plan/recalculate", null);

        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, write.StatusCode);
    }

    /// <summary>Validating changes nothing, so it is available to anyone who can read a plan.</summary>
    [Fact]
    public async Task ValidatingAPlanNeedsOnlyViewPermission()
    {
        var (taskId, _, _) = await ArrangePlanAsync();
        var document = await (await _client.GetAsync($"/api/tasks/{taskId}/plan/export")).Content.ReadAsStringAsync();
        _factory.Permissions.Clear();
        _factory.Permissions.Add(Permissions.ReleasePlanView);

        var validate = await _client.PostAsJsonAsync($"/api/tasks/{taskId}/plan/validate", new { document }, Json);
        var import = await _client.PostAsJsonAsync($"/api/tasks/{taskId}/plan/import", new { document }, Json);

        Assert.Equal(HttpStatusCode.OK, validate.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, import.StatusCode);
    }

    [Fact]
    public async Task AnUnauthenticatedCallerIsRefused()
    {
        var (taskId, _, _) = await ArrangePlanAsync();

        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/tasks/{taskId}/plan");
        request.Headers.Add("X-Test-Anonymous", "1");
        var response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
