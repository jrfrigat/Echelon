namespace ReleaseOrchestrator.Providers.Abstractions.Vcs;

/// <summary>
/// The one setting the polling ingestion needs, named once so a poll-mode provider and the poller
/// agree on it without the poller knowing which provider it is talking to.
/// </summary>
/// <remarks>
/// A poll interval is not a VCS concept — it is how often <em>this service</em> asks — so it lives
/// here as a neutral, well-known key rather than in any one adapter. A poll-mode provider declares a
/// setting under <see cref="IntervalKey"/>, and the poller reads that key from whatever connection it
/// sweeps. That is what keeps splitting <c>gitlab</c> into a webhook type and a poll type from
/// teaching the poller anything about GitLab.
/// </remarks>
public static class VcsPollSettings
{
    /// <summary>Settings-bag key holding how often, in seconds, the poller sweeps a connection.</summary>
    public const string IntervalKey = "pollIntervalSeconds";

    /// <summary>The interval assumed when a connection does not set one.</summary>
    public const int DefaultIntervalSeconds = 300;

    /// <summary>The shortest interval an operator may configure, so a typo cannot hammer the API.</summary>
    public const int MinIntervalSeconds = 30;

    /// <summary>The longest interval worth offering — a day.</summary>
    public const int MaxIntervalSeconds = 86_400;

    /// <summary>Reads the interval from a connection's settings, falling back to the default.</summary>
    /// <param name="settings">The connection's provider settings bag.</param>
    /// <returns>The configured interval in seconds, or <see cref="DefaultIntervalSeconds"/>.</returns>
    public static int IntervalFrom(IReadOnlyDictionary<string, string> settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        return settings.TryGetValue(IntervalKey, out var raw)
               && int.TryParse(raw, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var seconds)
            ? seconds
            : DefaultIntervalSeconds;
    }
}
