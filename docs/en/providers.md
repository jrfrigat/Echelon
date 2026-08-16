# Provider Architecture Guide

> [Русская версия ->](../ru/providers.md) - [← Back to docs](../README.md)

## Overview

A **provider** is an adapter that normalizes one dialect (API endpoints, authentication, state
strings) into a common port. Echelon registers providers with **compile-time keyed
dependency injection**, not dynamic discovery or plugins.

### Why compile-time DI?

- **Fail-fast** - an unknown provider type is caught when the API validates the operator's input, not at runtime.
- **No reflection** - no scanning for attributes, no assembly loading.
- **Clear dependencies** - the composition root lists every provider it registers.
- **Pinned versions** - provider package versions live in `Directory.Packages.props`, not resolved at runtime.

### The seam

A provider is split into two ports so that binding to a connection can do I/O (detecting a
self-hosted server's version) and still hand callers a ready-to-use object:

- **`IVcsProviderAdapter` / `ITrackerProviderAdapter`** - keyed by the provider type stored on the
  connection. `ConnectAsync(context, ct)` binds to one connection and returns a provider.
- **`IVcsProvider` / `ITrackerProvider`** - already bound to a connection; no method takes an API URL
  or token, because the instance already knows them.

Keyed DI can resolve by key but cannot *enumerate* keys, so each provider also registers a **marker
record** (`VcsProviderRegistration` / `TrackerProviderRegistration`). That is what lets the factory
answer "must be one of: gitlab-webhook, gitlab-poll" and lets the API validate a provider type before
writing it to the database.

## Provider settings

A connection carries provider-specific settings as an opaque bag (`ProviderSettingsJson` on the
entity). The adapter declares what those settings are through `SettingsSchema`, and the admin form is
rendered from that schema - nothing in the UI names a provider or a field.

```csharp
public enum ProviderSettingKind { Text = 0, Int = 1, Enum = 2, Regex = 3 }

public sealed record ProviderSettingSchema(
    string Key,                              // e.g. "orgId"
    string Label,                            // "Organization ID" (user-facing)
    string? Description = null,
    bool Required = false,                   // connection cannot be saved without it
    bool Secret = false,                     // write-only: never returned by the API
    ProviderSettingKind Kind = ProviderSettingKind.Text,
    IReadOnlyList<string>? Options = null,   // for Kind = Enum
    string? Default = null,                  // pre-fills the form
    int? Min = null,                         // for Kind = Int
    int? Max = null);
```

`GET /api/providers/vcs` and `GET /api/providers/trackers` return each registered provider's type and
schema. For example the tracker list:

```json
[
  {
    "ProviderType": "yandextracker-webhook",
    "Settings": [
      { "Key": "orgId", "Label": "Organization ID", "Required": true, "Kind": "Text" },
      { "Key": "closedStatuses", "Label": "Closed statuses", "Kind": "Text",
        "Default": "closed, cancelled, rejected, resolved" }
    ]
  }
]
```

Secret settings are encrypted with the same key ring as the access token; the API never returns their
values, and a blank secret on save means "keep the stored one".

## Linking a merge request to its task

There is **no** provider method that parses a task key from a branch. How a merge request names its
task is a per-connection convention, not a provider dialect, so it is a pair of connection settings a
VCS provider declares and the **ingestion** applies:

```csharp
// In Providers.Abstractions.Vcs.TaskLinkSettings
public const string SourceKey  = "taskKeySource";   // Enum: branch | title | label
public const string PatternKey = "taskKeyPattern";  // Regex; group 1 (or the whole match) is the key
public const string DefaultPattern = @"(?<![A-Za-z0-9])([A-Z][A-Z0-9]*-\d+)(?![A-Za-z0-9])";
```

A VCS adapter adds `TaskLinkSettings.Schema` to its `SettingsSchema`, and the ingestion builds the
rule with `TaskLinkSettings.RuleFrom(settings)` and applies it through
`Core.Parsing.TaskKeyExtractor.Extract(source, pattern, branch, title, labels)` - one pure, tested,
single copy of the rule. The connection form previews the extracted key live.

## Adding a VCS provider

Use the existing GitLab provider (`src/Echelon.Providers.GitLab/`) as the reference.

### 1. Implement the ports

```csharp
using Echelon.Providers.Abstractions.Vcs;

internal sealed class MyVcsAdapter(HttpClient http) : IVcsProviderAdapter
{
    // The fields the form should offer for this VCS. The linking rule is shared;
    // add anything vendor-specific alongside it.
    public IReadOnlyList<ProviderSettingSchema> SettingsSchema { get; } = TaskLinkSettings.Schema;

    public async Task<IVcsProvider> ConnectAsync(VcsProviderContext context, CancellationToken ct)
    {
        // context = (ConnectionName, ApiUrl, AccessToken). Provider-specific settings are NOT here:
        // the linking rule and poll interval are read by the ingestion, not by API calls.
        var capabilities = new VcsCapabilities { SupportsMergeRequestLabels = true };
        return new MyVcsProvider(http, context, capabilities);
    }
}

internal sealed class MyVcsProvider(HttpClient http, VcsProviderContext context, VcsCapabilities caps)
    : IVcsProvider
{
    public VcsCapabilities Capabilities { get; } = caps;

    public Task<VcsMergeRequest?> GetMergeRequestAsync(
        string projectPath, string mergeRequestId, CancellationToken ct) => /* GET one MR */;

    public Task<IReadOnlyList<VcsMergeRequest>> GetOpenMergeRequestsAsync(
        string projectPath, CancellationToken ct) => /* GET open MRs */;
}
```

The port is deliberately small - these are the calls the planner makes today. Normalize the vendor's
state strings to `MergeRequestStatus`, and populate `VcsMergeRequest.Labels` /
`VcsMergeRequest.PipelineStatus` so the readiness gate can read them.

### 2. Register - push and/or poll are distinct types

Push versus poll is a property of the provider *type*, not a per-connection toggle. A VCS that both
pushes and can be polled registers two types, each with its `IngestionMode`:

```csharp
public const string WebhookProviderType = "myvcs-webhook";
public const string PollProviderType    = "myvcs-poll";

public static IServiceCollection AddMyVcsProvider(this IServiceCollection services)
{
    services.AddHttpClient<MyVcsAdapter>(c => c.Timeout = TimeSpan.FromSeconds(30));

    services.AddKeyedScoped<IVcsProviderAdapter>(WebhookProviderType,
        (sp, _) => new MyVcsWebhookAdapter(sp.GetRequiredService<MyVcsAdapter>()));
    services.AddKeyedScoped<IVcsProviderAdapter>(PollProviderType,
        (sp, _) => new MyVcsPollAdapter(sp.GetRequiredService<MyVcsAdapter>()));

    // The marker records make the types discoverable and tell the poller which to sweep.
    services.AddSingleton(new VcsProviderRegistration(WebhookProviderType, IngestionMode.Push));
    services.AddSingleton(new VcsProviderRegistration(PollProviderType, IngestionMode.Poll));
    return services;
}
```

The **poll** type's `SettingsSchema` also declares the interval, and the poller reads it back:

```csharp
// poll adapter's schema = TaskLinkSettings.Schema + the interval field
new ProviderSettingSchema(VcsPollSettings.IntervalKey, "Poll interval (s)",
    Kind: ProviderSettingKind.Int, Default: "300",
    Min: VcsPollSettings.MinIntervalSeconds, Max: VcsPollSettings.MaxIntervalSeconds)
// the poller calls VcsPollSettings.IntervalFrom(connection.Settings)
```

### 3. Own the webhook (push types only)

A push provider owns everything vendor-specific about a delivery - payload shape, which header carries
the secret, how a state string maps to a status - behind `IWebhookParser`. The host keeps only the
route, secret resolution, and putting the resulting events on the bus.

```csharp
internal sealed class MyVcsWebhookParser : IWebhookParser
{
    public WebhookEndpointDescriptor Descriptor => /* endpoint name + which header carries the secret */;

    // Must fail closed and run in constant time - see WebhookSignatures.
    public bool Authenticate(WebhookRequest request, string? secret) => /* verify */;

    // Empty is a normal answer (an event this provider does not model); throw
    // WebhookPayloadException only for a malformed body.
    public IReadOnlyList<IngestionEvent> Parse(WebhookRequest request) => /* normalize */;
}

// Registered separately, so the ingress host gets the parser without the HTTP read-adapter:
services.AddKeyedSingleton<IWebhookParser, MyVcsWebhookParser>(WebhookProviderType);
services.AddSingleton(new WebhookParserRegistration(WebhookProviderType));
```

### 4. Deploy strategies

How a repository is deployed is an `IDeployStrategy`, keyed and paired with a
`DeployStrategyRegistration` (GitLab ships `gitlab-merge` and `gitlab-pipeline`). It is chosen per
`(repository, environment)` deploy target and declares its own `SettingsSchema`.

### 5. Wire into the composition root

```csharp
// src/Echelon.Infrastructure/InfrastructureExtensions.cs (API host)
services.AddGitLabProvider();
services.AddMyVcsProvider();          // ← the read-adapters + registrations
services.AddMyVcsDeployStrategies();

// ingress host wires the parsers instead
services.AddGitLabWebhookParser();
services.AddMyVcsWebhookParser();
```

Add the project reference to the relevant host `.csproj`, and the package version to
`Directory.Packages.props` (never a `Version` on the `PackageReference`).

## Adding a tracker provider

Use `src/Echelon.Providers.YandexTracker/` as the reference. A tracker adapter's context
**does** carry the settings bag (`TrackerProviderContext(ConnectionName, ApiUrl, AccessToken,
ProviderSettings)`), because the tracker adapter itself needs them (an org id header, which statuses
are "done").

```csharp
public sealed record MyTrackerOptions(string ProjectKey, IReadOnlySet<string> ClosedStatuses)
{
    public const string ProjectKeyKey     = "projectKey";
    public const string ClosedStatusesKey = "closedStatuses";

    public static MyTrackerOptions From(TrackerProviderContext context)
    {
        context.ProviderSettings.TryGetValue(ProjectKeyKey, out var key);
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException(
                $"Tracker '{context.ConnectionName}' requires '{ProjectKeyKey}'.");

        context.ProviderSettings.TryGetValue(ClosedStatusesKey, out var closed);
        var closedStatuses = string.IsNullOrWhiteSpace(closed)
            ? new HashSet<string>(["done", "closed"], StringComparer.OrdinalIgnoreCase)
            : closed.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return new MyTrackerOptions(key.Trim(), closedStatuses);
    }
}

internal sealed class MyTrackerAdapter(HttpClient http) : ITrackerProviderAdapter
{
    public IReadOnlyList<ProviderSettingSchema> SettingsSchema { get; } =
    [
        new(MyTrackerOptions.ProjectKeyKey, "Project key", Required: true),
        new(MyTrackerOptions.ClosedStatusesKey, "Closed statuses",
            Description: "Comma-separated status keys that mean a task is done.",
            Default: "done, closed"),
    ];

    public Task<ITrackerProvider> ConnectAsync(TrackerProviderContext context, CancellationToken ct)
        => Task.FromResult<ITrackerProvider>(new MyTrackerProvider(http, context, MyTrackerOptions.From(context)));
}
```

The provider implements `ITrackerProvider` (read an issue, decide whether a status is closed) and,
**optionally**, `ITrackerDependencySource` (issue links) and `ITrackerMutator` (write back). Optional
capabilities are `is`-checked by callers rather than returning empty lists that cannot be
distinguished from "genuinely none":

```csharp
public bool IsClosedStatus(string? statusKey) =>
    statusKey is not null && _options.ClosedStatuses.Contains(statusKey.Trim());
```

Making the closed set a setting (not a hardcoded list) is what lets one project call the terminal
state `done` and another `deployed`. Register with a keyed adapter plus the marker record:

```csharp
public const string ProviderType = "mytracker";
services.AddHttpClient<MyTrackerAdapter>(c => c.Timeout = TimeSpan.FromSeconds(30));
services.AddKeyedScoped<ITrackerProviderAdapter, MyTrackerAdapter>(ProviderType);
services.AddSingleton(new TrackerProviderRegistration(ProviderType));
```

## Capabilities

`VcsCapabilities` / `TrackerCapabilities` answer "what can *this connection* do", built once at
`ConnectAsync` and read-only after. `VcsCapabilities.SupportsMergeRequestLabels` distinguishes an
empty label set that means "no labels" from one that means "this install cannot report labels" - the
readiness gate must never read "cannot say" as "the label was removed". `ServerVersion` is detected
once and cached; `null` means "unknown", never "old", and compares numerically, never as text
("16.11" is newer than "16.9").

## The flow

1. An operator creates a connection in the UI and picks a `ProviderType` (e.g. `gitlab-webhook`).
2. The API validates it against the factory's registered types.
3. The connection is saved with that type and its settings bag.
4. When the orchestrator needs the provider, the factory does `GetRequiredKeyedService` by type,
   calls `ConnectAsync`, and returns a bound `IVcsProvider` / `ITrackerProvider`.
5. Domain code calls provider methods with no URL, token, or provider knowledge.
6. An unknown type yields `UnknownProviderException`, naming the registered ones.

**Ports are provider-agnostic; adapters are dialect-specific.** Credentials and config are bound at
connect time, not threaded through every method.
