# Provider Architecture Guide

## Overview

A **provider** is an adapter that normalizes dialect-specific details (API endpoints, authentication, state mappings) into a common interface. ReleaseOrchestrator uses **compile-time keyed dependency injection** to register providers, not dynamic discovery or plugins.

### Why Compile-Time DI?

- **Fail-fast:** Unknown provider types are caught at composition root setup, not at runtime when a user clicks a button
- **No reflection:** No scanning for attribute-marked classes or dynamic loading
- **Clear dependencies:** See at a glance which providers are available
- **Predictable versioning:** Provider versions are pinned in `.csproj`, not loaded from nuget.org at runtime

### How It Works

1. You create a new provider project (e.g., `ReleaseOrchestrator.Providers.GitHub`)
2. You implement the port interface (`IVcsProviderAdapter` or `ITrackerProviderAdapter`)
3. You declare provider-specific settings via `SettingsSchema` (see [Provider Settings](#provider-settings) below)
4. You call `.AddYourProvider()` in `InfrastructureExtensions` — one line
5. The factory discovers your provider via keyed DI and resolves it at runtime
6. If the key doesn't match any registered provider, `UnknownProviderException` lists the available ones

## Provider Settings

Providers can declare connection-specific settings (e.g., Yandex.Tracker requires an org ID, GitLab does not). Settings are discovered and validated via the provider APIs:

**`GET /api/providers/vcs`** — Lists registered VCS providers and their settings schema:
```json
[
  {
    "ProviderType": "github",
    "Settings": []
  },
  {
    "ProviderType": "gitlab",
    "Settings": []
  }
]
```

**`GET /api/providers/trackers`** — Lists registered tracker providers and their settings schema:
```json
[
  {
    "ProviderType": "yandex-tracker",
    "Settings": [
      {
        "Key": "orgId",
        "Label": "Organization ID",
        "Description": "The Yandex org UUID (from tracker URL)",
        "Required": true,
        "Secret": false
      }
    ]
  }
]
```

### Declaring Settings

Each provider declares its settings by implementing `IVcsProviderFactory.GetSettingsSchema(providerType)` or `ITrackerProviderFactory.GetSettingsSchema(providerType)`. The schema returns a list of `ProviderSettingSchema` records:

```csharp
public record ProviderSettingSchema(
    string Key,                           // e.g. "orgId"
    string Label,                         // "Organization ID" (user-facing)
    string? Description = null,           // "The Yandex org UUID..."
    bool Required = false,                // True if connection cannot be saved without it
    bool Secret = false                   // True for write-only fields (not returned by API)
);
```

**Why?** Before this, each provider's settings were hard-coded into the UI form and the API contract. Adding a new provider meant editing three places. Now the UI queries the API to discover what settings each provider needs and renders the form dynamically.

### Storing Settings

Provider settings are stored as opaque JSON in the connection entity:
- `VcsConnection.ProviderSettingsJson` (for VCS providers)
- `TrackerConnection.ProviderSettingsJson` (for tracker providers)

The provider adapter owns the schema and parsing logic:

```csharp
public class YandexTrackerOptions
{
    public required string OrgId { get; init; }

    public static YandexTrackerOptions From(TrackerProviderContext context)
    {
        var settings = context.ProviderSettings;  // Dict<string, string?>
        if (!settings.TryGetValue("orgId", out var orgId) 
            || string.IsNullOrWhiteSpace(orgId))
        {
            throw new InvalidOperationException(
                $"Tracker '{context.ConnectionName}' missing 'orgId' setting.");
        }

        return new YandexTrackerOptions { OrgId = orgId.Trim() };
    }
}
```

The Web API layer never parses provider settings directly — it passes the bag to the adapter, which interprets it.

## Adding a VCS Provider (e.g., GitHub)

### Step 1: Create the Project

```bash
mkdir src/ReleaseOrchestrator.Providers.GitHub
cat > src/ReleaseOrchestrator.Providers.GitHub/ReleaseOrchestrator.Providers.GitHub.csproj <<EOF
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
  </PropertyGroup>

  <ItemGroup>
    <ProjectReference Include="..\..\Providers.Abstractions\ReleaseOrchestrator.Providers.Abstractions.csproj" />
  </ItemGroup>

  <ItemGroup>
    <PackageReference Include="System.Net.Http" Version="4.3.4" />
  </ItemGroup>
</Project>
EOF
```

Add the project to `ReleaseOrchestrator.slnx`:

```bash
dotnet sln ReleaseOrchestrator.slnx add src/ReleaseOrchestrator.Providers.GitHub/ReleaseOrchestrator.Providers.GitHub.csproj
```

### Step 2: Implement `IVcsProviderAdapter`

```csharp
using ReleaseOrchestrator.Providers.Abstractions.Vcs;

namespace ReleaseOrchestrator.Providers.GitHub;

/// <summary>
/// Connects to a GitHub repository and creates an <see cref="IVcsProvider"/> instance.
/// </summary>
internal class GitHubProviderAdapter : IVcsProviderAdapter
{
    private readonly HttpClient _client;

    public GitHubProviderAdapter(HttpClient client)
    {
        _client = client;
    }

    public async Task<IVcsProvider> ConnectAsync(VcsProviderContext context, CancellationToken ct = default)
    {
        // Optionally: Detect server version and build capabilities
        var version = await DetectVersionAsync(context.ApiUrl, context.AccessToken, ct);
        var capabilities = new VcsCapabilities(
            ServerVersion: version?.ToString(),
            SupportsMergeRequestLabels: version?.IsAtLeast(3, 15) ?? true
        );

        return new GitHubProvider(_client, context, capabilities);
    }

    private static async Task<GitHubServerVersion?> DetectVersionAsync(
        Uri apiUrl,
        string accessToken,
        CancellationToken ct)
    {
        try
        {
            // GitHub API doesn't expose version like GitLab does, so version detection may be empty
            // Return null if not supported
            return null;
        }
        catch
        {
            return null; // Failure is conservative: unknown version keeps defaults on
        }
    }
}

/// <summary>
/// The normalized VCS provider for GitHub.
/// </summary>
internal class GitHubProvider : IVcsProvider
{
    private readonly HttpClient _client;
    private readonly VcsProviderContext _context;

    public GitHubProvider(HttpClient client, VcsProviderContext context, VcsCapabilities capabilities)
    {
        _client = client;
        _context = context;
        Capabilities = capabilities;
    }

    public VcsCapabilities Capabilities { get; }

    public async Task<VcsMergeRequest?> GetMergeRequestAsync(
        string repositoryExternalId,
        string mergeRequestExternalId,
        CancellationToken ct = default)
    {
        // GitHub API: GET /repos/{owner}/{repo}/pulls/{number}
        var url = $"{_context.ApiUrl}/repos/{repositoryExternalId}/pulls/{mergeRequestExternalId}";
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("Authorization", $"token {_context.AccessToken}");

        var response = await _client.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            return null;

        var json = await response.Content.ReadAsStringAsync(ct);
        // Parse JSON, normalize to VcsMergeRequest
        return ParsePullRequest(json);
    }

    public async Task<IEnumerable<VcsMergeRequest>> GetOpenMergeRequestsAsync(
        string repositoryExternalId,
        CancellationToken ct = default)
    {
        // GitHub API: GET /repos/{owner}/{repo}/pulls?state=open
        var url = $"{_context.ApiUrl}/repos/{repositoryExternalId}/pulls?state=open&per_page=100";
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("Authorization", $"token {_context.AccessToken}");

        var response = await _client.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            return [];

        var json = await response.Content.ReadAsStringAsync(ct);
        // Parse JSON array, normalize to VcsMergeRequest[]
        return ParsePullRequests(json);
    }

    public string? ParseTaskKeyFromBranch(string branchName)
    {
        // Use the same regex as GitLab: (?<![A-Za-z0-9])([A-Z][A-Z0-9]*-\d+)(?![A-Za-z0-9])
        var match = System.Text.RegularExpressions.Regex.Match(
            branchName,
            @"(?<![A-Za-z0-9])([A-Z][A-Z0-9]*-\d+)(?![A-Za-z0-9])");
        return match.Success ? match.Groups[1].Value : null;
    }

    private static VcsMergeRequest? ParsePullRequest(string json)
    {
        // Use System.Text.Json or a JSON library to parse and map to VcsMergeRequest
        // This is a stub—see GitLab provider for a real example
        return null;
    }

    private static IEnumerable<VcsMergeRequest> ParsePullRequests(string json)
    {
        // Similar parsing for array
        return [];
    }
}
```

### Step 3: Create the Registration Extension

```csharp
using Microsoft.Extensions.DependencyInjection;
using ReleaseOrchestrator.Providers.Abstractions;
using ReleaseOrchestrator.Providers.Abstractions.Vcs;

namespace ReleaseOrchestrator.Providers.GitHub;

public static class GitHubProviderExtensions
{
    /// <summary>
    /// The normalized key used in <see cref="VcsConnection.ProviderType"/>.
    /// </summary>
    public const string ProviderType = "github";

    /// <summary>
    /// Registers the GitHub VCS provider adapter.
    /// </summary>
    public static IServiceCollection AddGitHubProvider(this IServiceCollection services)
    {
        services
            .AddHttpClient<GitHubProviderAdapter>()
            .ConfigureHttpClient(client =>
            {
                client.Timeout = TimeSpan.FromSeconds(30);
            });

        services.AddKeyedScoped<IVcsProviderAdapter, GitHubProviderAdapter>(ProviderType);

        services.AddSingleton(new VcsProviderRegistration(ProviderType));

        return services;
    }
}
```

### Step 4: Register in Composition Root

Open `src/ReleaseOrchestrator.Infrastructure/InfrastructureExtensions.cs` and add:

```csharp
public static IServiceCollection AddInfrastructure(
    this IServiceCollection services,
    IConfiguration configuration)
{
    // ... existing code ...

    services.AddGitLabProvider();
    services.AddGitHubProvider();        // ← Add this line
    services.AddYandexTrackerProvider();

    return services;
}
```

Add the project reference to `Infrastructure.csproj`:

```xml
<ItemGroup>
  <ProjectReference Include="..\ReleaseOrchestrator.Providers.Abstractions\ReleaseOrchestrator.Providers.Abstractions.csproj" />
  <ProjectReference Include="..\ReleaseOrchestrator.Providers.GitLab\ReleaseOrchestrator.Providers.GitLab.csproj" />
  <ProjectReference Include="..\ReleaseOrchestrator.Providers.GitHub\ReleaseOrchestrator.Providers.GitHub.csproj" />
  <!-- ... etc -->
</ItemGroup>
```

### Step 5: Write Tests

```csharp
using ReleaseOrchestrator.Providers.Abstractions.Vcs;
using ReleaseOrchestrator.Providers.GitHub;
using Xunit;

namespace ReleaseOrchestrator.UnitTests.Providers.GitHub;

public class GitHubProviderTests
{
    [Fact]
    public void ParsesTaskKeyFromBranch()
    {
        var provider = new GitHubProvider(new HttpClient(), new VcsProviderContext(
            ConnectionName: "github",
            ApiUrl: new Uri("https://api.github.com"),
            AccessToken: "token",
            ReadyForDeployLabel: "ready-for-deploy"
        ), new VcsCapabilities());

        Assert.Equal("PROJ-123", provider.ParseTaskKeyFromBranch("feature/PROJ-123-add-feature"));
        Assert.Null(provider.ParseTaskKeyFromBranch("main"));
    }
}
```

## Adding a Tracker Provider (e.g., Jira)

### Step 1: Create the Project

```bash
mkdir src/ReleaseOrchestrator.Providers.Jira
dotnet sln ReleaseOrchestrator.slnx add src/ReleaseOrchestrator.Providers.Jira/ReleaseOrchestrator.Providers.Jira.csproj
```

### Step 2: Implement `ITrackerProviderAdapter`

```csharp
using ReleaseOrchestrator.Providers.Abstractions.Tracker;

namespace ReleaseOrchestrator.Providers.Jira;

/// <summary>
/// Jira-specific options read from TrackerConnection.ProviderSettingsJson.
/// </summary>
internal class JiraOptions
{
    public required string ProjectKey { get; init; }

    public static JiraOptions From(TrackerProviderContext context)
    {
        var settings = context.ProviderSettings;
        if (!settings.TryGetValue("projectKey", out var projectKey)
            || string.IsNullOrWhiteSpace(projectKey))
        {
            throw new InvalidOperationException(
                $"Tracker connection '{context.ConnectionName}' is missing 'projectKey' setting.");
        }

        return new JiraOptions { ProjectKey = projectKey.Trim() };
    }
}

internal class JiraProviderAdapter : ITrackerProviderAdapter
{
    private readonly HttpClient _client;

    public JiraProviderAdapter(HttpClient client)
    {
        _client = client;
    }

    public async Task<ITrackerProvider> ConnectAsync(
        TrackerProviderContext context,
        CancellationToken ct = default)
    {
        var options = JiraOptions.From(context);
        var capabilities = new TrackerCapabilities(ServerVersion: null);

        return new JiraProvider(_client, context, options, capabilities);
    }
}

internal class JiraProvider : ITrackerProvider, ITrackerDependencySource
{
    private readonly HttpClient _client;
    private readonly TrackerProviderContext _context;
    private readonly JiraOptions _options;

    public JiraProvider(
        HttpClient client,
        TrackerProviderContext context,
        JiraOptions options,
        TrackerCapabilities capabilities)
    {
        _client = client;
        _context = context;
        _options = options;
        Capabilities = capabilities;
    }

    public TrackerCapabilities Capabilities { get; }

    public async Task<TrackerIssue?> GetIssueAsync(
        string externalTaskId,
        CancellationToken ct = default)
    {
        // Jira API: GET /rest/api/3/issue/{issueIdOrKey}
        var url = $"{_context.ApiUrl}/rest/api/3/issue/{externalTaskId}";
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("Authorization", $"Bearer {_context.AccessToken}");

        var response = await _client.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            return null;

        var json = await response.Content.ReadAsStringAsync(ct);
        return ParseIssue(json);
    }

    public bool IsClosedStatus(string? statusKey)
    {
        // Jira status keys vary per project; "Done" is common
        return statusKey?.Equals("Done", StringComparison.OrdinalIgnoreCase) ?? false;
    }

    public async Task<IEnumerable<TrackerIssueDependency>> GetIssueDependenciesAsync(
        string externalTaskId,
        CancellationToken ct = default)
    {
        // Jira API: GET /rest/api/3/issue/{issueIdOrKey}?expand=changelog
        // Parse issue links and filter by link type "depends on"
        var url = $"{_context.ApiUrl}/rest/api/3/issue/{externalTaskId}?expand=changelog";
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Add("Authorization", $"Bearer {_context.AccessToken}");

        var response = await _client.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            return [];

        var json = await response.Content.ReadAsStringAsync(ct);
        return ParseIssueDependencies(json);
    }

    private static TrackerIssue? ParseIssue(string json)
    {
        // Use System.Text.Json to parse and map
        return null;
    }

    private static IEnumerable<TrackerIssueDependency> ParseIssueDependencies(string json)
    {
        return [];
    }
}
```

### Step 3: Registration Extension

```csharp
using Microsoft.Extensions.DependencyInjection;
using ReleaseOrchestrator.Providers.Abstractions;
using ReleaseOrchestrator.Providers.Abstractions.Tracker;

namespace ReleaseOrchestrator.Providers.Jira;

public static class JiraProviderExtensions
{
    public const string ProviderType = "jira";

    public static IServiceCollection AddJiraProvider(this IServiceCollection services)
    {
        services
            .AddHttpClient<JiraProviderAdapter>()
            .ConfigureHttpClient(client =>
            {
                client.Timeout = TimeSpan.FromSeconds(30);
            });

        services.AddKeyedScoped<ITrackerProviderAdapter, JiraProviderAdapter>(ProviderType);
        services.AddSingleton(new TrackerProviderRegistration(ProviderType));

        return services;
    }
}
```

### Step 4: Register in Composition Root

In `InfrastructureExtensions.cs`:

```csharp
services.AddGitLabProvider();
services.AddYandexTrackerProvider();
services.AddJiraProvider();  // ← Add this line
```

## Capabilities Mechanism

Providers expose capabilities in three ways:

### 1. Optional Interfaces (is-check pattern)

Some trackers have issue links, others don't. Instead of a method that returns an empty list, implement the optional interface:

```csharp
public interface ITrackerDependencySource
{
    Task<IEnumerable<TrackerIssueDependency>> GetIssueDependenciesAsync(
        string externalTaskId,
        CancellationToken ct = default);
}
```

Consumer checks before calling:

```csharp
if (provider is ITrackerDependencySource source)
{
    var deps = await source.GetIssueDependenciesAsync(issueKey, ct);
    // Use deps
}
else
{
    // No dependency support
}
```

### 2. Per-Connection Capabilities Record

Capabilities tied to a specific connection instance:

```csharp
public record VcsCapabilities(
    string? ServerVersion,
    bool SupportsMergeRequestLabels
);
```

Built at `ConnectAsync` time, read-only thereafter. Example: GitLab 9.0+ supports labels; earlier versions don't.

### 3. Version Detection with Caching

For expensive API calls, cache per URL (singleton):

```csharp
internal class GitLabVersionDetector
{
    private readonly Dictionary<Uri, GitLabServerVersion?> _cache = new();

    public async Task<GitLabServerVersion?> DetectAsync(Uri apiUrl, string token, CancellationToken ct)
    {
        if (_cache.TryGetValue(apiUrl, out var cached))
            return cached;

        try
        {
            var response = await _client.GetAsync($"{apiUrl}/api/v4/version", ct);
            var version = ParseVersion(response);
            _cache[apiUrl] = version;
            return version;
        }
        catch
        {
            _cache[apiUrl] = null; // Conservative: null = unknown, keep defaults on
            return null;
        }
    }
}
```

Register as singleton; adapter calls it once per connect.

## Version Ordering

If your API reports a version, use numeric comparison, not text comparison:

```csharp
public record GitLabServerVersion(int Major, int Minor, int Patch)
{
    public bool IsAtLeast(int major, int minor) =>
        Major > major || (Major == major && Minor >= minor);

    public static bool operator <(GitLabServerVersion a, GitLabServerVersion b) =>
        a.Major < b.Major || (a.Major == b.Major && a.Minor < b.Minor)
        || (a.Major == b.Major && a.Minor == b.Minor && a.Patch < b.Patch);
}
```

Text comparison of "16.11" < "16.9" gives the wrong answer.

## Patterns from Existing Providers

### GitLab (`src/ReleaseOrchestrator.Providers.GitLab/`)

**State Mapping:**

```csharp
public static MergeRequestStatus? FromState(string? state) => state?.ToLowerInvariant() switch
{
    "opened" => MergeRequestStatus.Opened,
    "merged" => MergeRequestStatus.Merged,
    "closed" => MergeRequestStatus.Closed,
    _ => null,
};
```

GitLab returns strings; normalize to domain enum. Unknown states return `null` (handled by domain, not adapter).

**Branch Parsing:**

```csharp
private static readonly System.Text.RegularExpressions.Regex BranchTaskRegex =
    new(@"(?<![A-Za-z0-9])([A-Z][A-Z0-9]*-\d+)(?![A-Za-z0-9])", RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));

public static string? ParseTaskId(string branchName)
{
    var match = BranchTaskRegex.Match(branchName);
    return match.Success ? match.Groups[1].Value : null;
}
```

Compiled regex with timeout. Pattern: `PROJ-123`, not `proj123` (case-sensitive key).

**Reusable by Webhook:** Both `GitLabWebhookEndpoints` and `VcsService.SyncMergeRequestAsync` call `GitLabMergeRequestState.FromState`, ensuring they never disagree on state interpretation.

### Yandex.Tracker (`src/ReleaseOrchestrator.Providers.YandexTracker/`)

**Closed Status Rules:**

```csharp
private static readonly HashSet<string> ClosedStatuses =
    new(new[] { "closed", "cancelled", "rejected", "resolved" }, StringComparer.OrdinalIgnoreCase);

public static bool IsClosed(string? statusKey) =>
    !string.IsNullOrWhiteSpace(statusKey) && ClosedStatuses.Contains(statusKey);
```

Single source of truth for what "closed" means. Before, two copies of this list disagreed on "resolved" — one included it, one didn't, leaving tasks stuck.

**Typed Options:**

```csharp
public class YandexTrackerOptions
{
    public required string OrgId { get; init; }

    public static YandexTrackerOptions From(TrackerProviderContext context)
    {
        var settings = context.ProviderSettings; // Dict<string, string?>
        if (!settings.TryGetValue("orgId", out var orgId) || string.IsNullOrWhiteSpace(orgId))
            throw new InvalidOperationException($"Tracker '{context.ConnectionName}' missing orgId");
        return new YandexTrackerOptions { OrgId = orgId.Trim() };
    }
}
```

Provider-specific config lives in opaque JSON in the database; adapter owns the schema. Web controller reads/writes `OrgId` via helpers, doesn't parse JSON itself.

**Dependency Link Type:**

```csharp
public async Task<IEnumerable<TrackerIssueDependency>> GetIssueDependenciesAsync(
    string externalTaskId,
    CancellationToken ct = default)
{
    // Fetch issue, extract links where type.id == "depends" (i.e., this issue depends on others)
    var issue = await GetIssueAsync(externalTaskId, ct);
    if (issue?.Links == null)
        return [];

    return issue.Links
        .Where(link => link.Type.Id == "depends")
        .Select(link => new TrackerIssueDependency(
            IssueKey: externalTaskId,
            DependsOnKey: link.Object.Key))
        .ToList();
}
```

Direction matters: `"depends"` means the issue **depends on** the linked issue.

## Testing Providers

### Unit Test Example

```csharp
public class GitLabProviderTests
{
    private readonly GitLabProvider _provider;

    public GitLabProviderTests()
    {
        var client = new HttpClient();
        var context = new VcsProviderContext(
            ConnectionName: "test-gitlab",
            ApiUrl: new Uri("https://gitlab.example.com"),
            AccessToken: "test-token",
            ReadyForDeployLabel: "ready-for-deploy");
        var capabilities = new VcsCapabilities(ServerVersion: "16.11.0", SupportsMergeRequestLabels: true);

        _provider = new GitLabProvider(client, context, capabilities);
    }

    [Theory]
    [InlineData("PROJ-123", "feature/PROJ-123-add-foo", true)]
    [InlineData("PROJ-123", "PROJ-123-add-foo", true)]
    [InlineData("PROJ-123", "myPROJ-123-branch", false)] // No word boundary before PROJ
    public void ParsesTaskKeyFromBranch(string expected, string branch, bool shouldMatch)
    {
        var result = _provider.ParseTaskKeyFromBranch(branch);
        if (shouldMatch)
            Assert.Equal(expected, result);
        else
            Assert.Null(result);
    }
}
```

Mock HTTP responses if testing API integration:

```csharp
var mockHandler = new Mock<HttpMessageHandler>();
mockHandler
    .Protected()
    .Setup<Task<HttpResponseMessage>>(
        "SendAsync",
        ItExpr.IsAny<HttpRequestMessage>(),
        ItExpr.IsAny<CancellationToken>())
    .ReturnsAsync(new HttpResponseMessage
    {
        StatusCode = System.Net.HttpStatusCode.OK,
        Content = new StringContent(@"{ ... }")
    });

var client = new HttpClient(mockHandler.Object);
```

## Summary: The Flow

1. **User creates a connection** in the UI, sets `ProviderType = "github"`
2. **API validates** against `IVcsProviderFactory.AvailableProviders` (filled from registrations)
3. **Entity saved** to database with `ProviderType = "github"`
4. **Sync runs:** calls `factory.CreateAsync(connection)` → keyed DI resolves `GitHubProviderAdapter` → calls `ConnectAsync` → returns `IVcsProvider`
5. **Domain logic calls** provider methods: `GetMergeRequestAsync(repo.ExternalId, mr.Id)` — no `apiUrl`, no token, no provider knowledge
6. **If unknown type:** `UnknownProviderException` names registered providers and suggests fixing the typo

The key insight: **Ports are provider-agnostic; adapters are dialect-specific.** Credentials and config are bound at factory time, not threaded through method signatures.
