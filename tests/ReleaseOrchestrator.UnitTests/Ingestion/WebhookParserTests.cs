using System.Text;
using Microsoft.Extensions.DependencyInjection;
using ReleaseOrchestrator.Core.Enums;
using ReleaseOrchestrator.Providers.Abstractions.Ingestion;
using ReleaseOrchestrator.Providers.GitLab;
using ReleaseOrchestrator.Providers.YandexTracker;
using Xunit;

namespace ReleaseOrchestrator.UnitTests.Ingestion;

/// <summary>
/// Exercises the provider webhook parsers through the public port, the way the host does.
/// </summary>
/// <remarks>
/// The parsers are internal; they are reached the same way the ingress reaches them — resolved from
/// the container by provider key — so these tests also prove the registration exists and is keyed
/// correctly. There is no integration-test host for the endpoint itself (the unit project does not
/// reference the ingress), so the endpoint's own behaviour — the 401, the 400, the bus send — is
/// covered by the live check in docs/issues/010, not here.
/// </remarks>
public class WebhookParserTests
{
    private static IWebhookParser GitLab() =>
        new ServiceCollection().AddGitLabWebhookParser().BuildServiceProvider()
            .GetRequiredKeyedService<IWebhookParser>(GitLabProviderExtensions.ProviderType);

    private static IWebhookParser Yandex() =>
        new ServiceCollection().AddYandexTrackerWebhookParser().BuildServiceProvider()
            .GetRequiredKeyedService<IWebhookParser>(YandexTrackerProviderExtensions.ProviderType);

    private static WebhookRequest Request(string json, params (string Key, string Value)[] headers) =>
        new(
            connectionName: "conn",
            body: Encoding.UTF8.GetBytes(json),
            headers: headers.ToDictionary(h => h.Key, h => h.Value, StringComparer.OrdinalIgnoreCase));

    // ---- GitLab: parsing -------------------------------------------------------

    [Fact]
    public void GitLabOpenedMergeRequestBecomesAnOpenedEvent()
    {
        const string json = """
        {
          "object_kind": "merge_request",
          "project": { "id": 1, "path_with_namespace": "group/app" },
          "object_attributes": {
            "iid": 42, "source_branch": "feature/PROJ-7-thing", "target_branch": "main",
            "state": "opened"
          },
          "labels": [ { "title": "Ready-For-Prod" }, { "title": "bug" } ]
        }
        """;

        var events = GitLab().Parse(Request(json));

        var opened = Assert.IsType<MrOpenedEvent>(Assert.Single(events));
        Assert.Equal("group/app", opened.RepositoryExternalId);
        Assert.Equal("42", opened.ExternalMrId);
        Assert.Equal("main", opened.TargetBranch);
        Assert.Equal("PROJ-7", opened.TaskExternalId);
        // Normalized through LabelSet: lower-cased, sorted, de-duplicated.
        Assert.Equal(["bug", "ready-for-prod"], opened.Labels);
    }

    /// <summary>
    /// GitLab reports labels at the top level and, for some events, only under object_attributes;
    /// the parser reads whichever is present. A JSON null in the array must not crash it.
    /// </summary>
    [Fact]
    public void GitLabReadsLabelsFromObjectAttributesAndSurvivesANullLabel()
    {
        const string json = """
        {
          "object_kind": "merge_request",
          "project": { "path_with_namespace": "group/app" },
          "object_attributes": {
            "iid": 1, "source_branch": "b", "target_branch": "main", "state": "opened",
            "labels": [ { "title": "qa" }, null, { "title": null } ]
          }
        }
        """;

        var opened = Assert.IsType<MrOpenedEvent>(Assert.Single(GitLab().Parse(Request(json))));
        Assert.Equal(["qa"], opened.Labels);
    }

    [Fact]
    public void GitLabMergedStateBecomesAStatusChange()
    {
        const string json = """
        {
          "object_kind": "merge_request",
          "project": { "path_with_namespace": "group/app" },
          "object_attributes": { "iid": 9, "source_branch": "b", "target_branch": "main", "state": "merged" }
        }
        """;

        var changed = Assert.IsType<MrStatusChangedEvent>(Assert.Single(GitLab().Parse(Request(json))));
        Assert.Equal(MergeRequestStatus.Merged, changed.NewStatus);
        Assert.Equal("9", changed.ExternalMrId);
    }

    [Fact]
    public void GitLabNonMergeRequestEventYieldsNothing() =>
        Assert.Empty(GitLab().Parse(Request("""{ "object_kind": "push" }""")));

    [Fact]
    public void GitLabUnmodelledStateYieldsNothing()
    {
        const string json = """
        {
          "object_kind": "merge_request",
          "project": { "path_with_namespace": "group/app" },
          "object_attributes": { "iid": 1, "source_branch": "b", "target_branch": "main", "state": "locked" }
        }
        """;

        Assert.Empty(GitLab().Parse(Request(json)));
    }

    [Fact]
    public void GitLabPayloadMissingRequiredFieldsIsRejected()
    {
        // merge_request event, but no project path — the one thing that identifies the repository.
        const string json = """
        {
          "object_kind": "merge_request",
          "object_attributes": { "iid": 1, "state": "opened" }
        }
        """;

        Assert.Throws<WebhookPayloadException>(() => GitLab().Parse(Request(json)));
    }

    [Fact]
    public void GitLabDeliveryIdBecomesTheDedupKey()
    {
        const string json = """
        {
          "object_kind": "merge_request",
          "project": { "path_with_namespace": "group/app" },
          "object_attributes": { "iid": 1, "source_branch": "b", "target_branch": "main", "state": "opened" }
        }
        """;

        var opened = Assert.IsType<MrOpenedEvent>(
            Assert.Single(GitLab().Parse(Request(json, ("X-Gitlab-Event-UUID", "abc-123")))));
        Assert.Equal("abc-123", opened.ProviderEventId);
    }

    // ---- GitLab: authentication ------------------------------------------------

    [Fact]
    public void GitLabAuthenticatesOnMatchingTokenOnly()
    {
        var parser = GitLab();
        var request = Request("{}", ("X-Gitlab-Token", "s3cret"));

        Assert.True(parser.Authenticate(request, "s3cret"));
        Assert.False(parser.Authenticate(request, "wrong"));
    }

    /// <summary>The same delivery authenticates against one connection's secret and not another's.</summary>
    [Fact]
    public void GitLabAuthenticationIsPerSecret()
    {
        var parser = GitLab();
        var request = Request("{}", ("X-Gitlab-Token", "token-A"));

        Assert.True(parser.Authenticate(request, "token-A"));
        Assert.False(parser.Authenticate(request, "token-B"));
    }

    [Fact]
    public void GitLabFailsClosedWhenSecretOrHeaderIsAbsent()
    {
        var parser = GitLab();

        // No configured secret -> reject, whatever the request carries.
        Assert.False(parser.Authenticate(Request("{}", ("X-Gitlab-Token", "anything")), null));
        Assert.False(parser.Authenticate(Request("{}", ("X-Gitlab-Token", "anything")), ""));
        // No header on the request -> reject.
        Assert.False(parser.Authenticate(Request("{}"), "s3cret"));
    }

    [Fact]
    public void GitLabTokenHeaderIsCaseInsensitive()
    {
        var parser = GitLab();
        // HTTP header names are case-insensitive; the lookup must not depend on GitLab's exact casing.
        Assert.True(parser.Authenticate(Request("{}", ("x-gitlab-token", "s3cret")), "s3cret"));
    }

    [Fact]
    public void GitLabDescriptorPreservesItsProductionStrings()
    {
        var d = GitLab().Descriptor;
        Assert.Equal("gitlab", d.ProviderType);
        Assert.Equal("gitlab", d.RouteSegment);
        Assert.Equal("GitLab", d.SecretConfigSection);
        Assert.Equal("gitlab", d.SourcePrefix);
    }

    // ---- Yandex.Tracker --------------------------------------------------------

    [Fact]
    public void YandexIssueCreatedBecomesATaskCreatedEvent()
    {
        const string json = """
        { "event": "issue:created", "issue": { "key": "PROJ-7", "summary": "Do the thing" } }
        """;

        var created = Assert.IsType<TaskCreatedEvent>(Assert.Single(Yandex().Parse(Request(json))));
        Assert.Equal("PROJ-7", created.ExternalId);
        Assert.Equal("Do the thing", created.Title);
    }

    [Fact]
    public void YandexClosedStatusIsReportedClosed()
    {
        const string json = """
        { "event": "issue:statusUpdated", "issue": { "key": "PROJ-7", "status": { "key": "closed" } } }
        """;

        var changed = Assert.IsType<TaskStatusChangedEvent>(Assert.Single(Yandex().Parse(Request(json))));
        Assert.Equal("closed", changed.NewStatus);
        Assert.True(changed.Closed);
    }

    [Fact]
    public void YandexOpenStatusIsReportedNotClosed()
    {
        const string json = """
        { "event": "issue:updated", "issue": { "key": "PROJ-7", "status": { "key": "inProgress" } } }
        """;

        var changed = Assert.IsType<TaskStatusChangedEvent>(Assert.Single(Yandex().Parse(Request(json))));
        Assert.False(changed.Closed);
    }

    [Fact]
    public void YandexMissingIssueKeyIsRejected() =>
        Assert.Throws<WebhookPayloadException>(() =>
            Yandex().Parse(Request("""{ "event": "issue:created", "issue": { "summary": "x" } }""")));

    [Fact]
    public void YandexStatusUpdateMissingStatusKeyIsRejected() =>
        Assert.Throws<WebhookPayloadException>(() =>
            Yandex().Parse(Request("""{ "event": "issue:updated", "issue": { "key": "PROJ-7" } }""")));

    [Fact]
    public void YandexUnknownEventYieldsNothing() =>
        Assert.Empty(Yandex().Parse(Request("""{ "event": "issue:commented", "issue": { "key": "PROJ-7" } }""")));

    [Fact]
    public void YandexDescriptorPreservesItsFourProductionStrings()
    {
        var d = Yandex().Descriptor;
        // The registered key and the three declared strings deliberately differ — preserved so the
        // refactor renames no route, no config key, and orphans no dedup rows.
        Assert.Equal("yandextracker", d.ProviderType);
        Assert.Equal("tracker", d.RouteSegment);
        Assert.Equal("Tracker", d.SecretConfigSection);
        Assert.Equal("yandex", d.SourcePrefix);
    }
}

/// <summary>Covers the shared constant-time credential check and the neutral request wrapper.</summary>
public class WebhookSignaturesTests
{
    [Fact]
    public void EqualSecretsMatch() =>
        Assert.True(WebhookSignatures.FixedTimeEquals("s3cret", "s3cret"));

    [Fact]
    public void DifferentSecretsDoNotMatch() =>
        Assert.False(WebhookSignatures.FixedTimeEquals("s3cret", "other"));

    /// <summary>
    /// A length difference must not be a shortcut: both sides are hashed to a fixed width first, so
    /// a shorter or longer guess is compared in constant time rather than rejected on length.
    /// </summary>
    [Fact]
    public void ALengthProbeIsRejectedLikeAnyMismatch()
    {
        Assert.False(WebhookSignatures.FixedTimeEquals("s", "s3cret-and-then-some"));
        Assert.False(WebhookSignatures.FixedTimeEquals("s3cret-and-then-some", "s"));
    }

    [Fact]
    public void EmptyOrNullFailsClosed()
    {
        Assert.False(WebhookSignatures.FixedTimeEquals(null, "s3cret"));
        Assert.False(WebhookSignatures.FixedTimeEquals("s3cret", null));
        Assert.False(WebhookSignatures.FixedTimeEquals("", ""));
    }

    [Fact]
    public void WebhookRequestHeaderLookupIsCaseInsensitive()
    {
        var request = new WebhookRequest(
            "conn", ReadOnlyMemory<byte>.Empty,
            new Dictionary<string, string> { ["X-Gitlab-Token"] = "v" });

        Assert.Equal("v", request.Header("x-gitlab-token"));
        Assert.Null(request.Header("absent"));
    }
}
