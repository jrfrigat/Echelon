using ReleaseOrchestrator.Core.Entities;
using ReleaseOrchestrator.Providers.Abstractions;
using ReleaseOrchestrator.Providers.Abstractions.Tracker;

namespace ReleaseOrchestrator.UnitTests.Tracker;

/// <summary>
/// A tracker held in dictionaries, standing in for a real one.
/// </summary>
/// <remarks>
/// Hand-written rather than mocked: these tests are about what TrackerService does with the
/// answers, and a mock framework would put expectation-setting between the reader and that. It
/// also counts calls, which is how the "fetch a prerequisite once, not the whole graph" claim is
/// checked at all.
/// </remarks>
internal sealed class FakeTrackerProvider : ITrackerProvider, ITrackerDependencySource
{
    private readonly Dictionary<string, TrackerIssue> _issues = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<string>> _dependsOn = new(StringComparer.OrdinalIgnoreCase);

    // Implementing ITrackerDependencySource is itself the statement that this tracker has links —
    // it is not a flag on Capabilities, because "does the provider do it at all" is answered by
    // the type, not by a value read at runtime.
    public TrackerCapabilities Capabilities { get; } = TrackerCapabilities.None;

    /// <summary>Issue keys requested, in order. Duplicates are the point.</summary>
    public List<string> IssueReads { get; } = [];

    public FakeTrackerProvider WithIssue(string key, string status = "open", DateTime? resolvedAt = null)
    {
        _issues[key] = new TrackerIssue(key, $"Summary of {key}", status, resolvedAt);
        return this;
    }

    /// <summary>Records that <paramref name="key"/> depends on <paramref name="dependsOnKeys"/>.</summary>
    public FakeTrackerProvider WithDependencies(string key, params string[] dependsOnKeys)
    {
        _dependsOn[key] = [.. dependsOnKeys];
        return this;
    }

    public Task<TrackerIssue?> GetIssueAsync(string issueKey, CancellationToken ct)
    {
        IssueReads.Add(issueKey);
        return Task.FromResult(_issues.GetValueOrDefault(issueKey));
    }

    public Task<IReadOnlyList<TrackerIssueDependency>> GetIssueDependenciesAsync(string issueKey, CancellationToken ct)
    {
        var edges = _dependsOn.TryGetValue(issueKey, out var keys)
            ? keys.Select(k => new TrackerIssueDependency(issueKey, k)).ToList()
            : [];

        return Task.FromResult<IReadOnlyList<TrackerIssueDependency>>(edges);
    }

    /// <summary>Yandex.Tracker's vocabulary, which is what the real adapter under test uses.</summary>
    public bool IsClosedStatus(string? statusKey) =>
        statusKey is "closed" or "cancelled" or "rejected" or "resolved";
}

/// <summary>Hands out one prepared provider, whatever connection it is asked about.</summary>
internal sealed class FakeTrackerProviderFactory(ITrackerProvider provider) : ITrackerProviderFactory
{
    public IReadOnlyCollection<string> AvailableProviders { get; } = ["fake"];

    public IReadOnlyList<ProviderSettingSchema> GetSettingsSchema(string providerType) => [];

    public Task<ITrackerProvider> CreateAsync(TrackerConnection connection, CancellationToken ct) =>
        Task.FromResult(provider);
}
