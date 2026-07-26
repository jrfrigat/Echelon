using ReleaseOrchestrator.Core.Enums;

namespace ReleaseOrchestrator.Providers.Abstractions.Vcs;

/// <summary>
/// The connection settings that configure how a merge request is linked to its task, named once so a
/// provider declares them and the ingestion reads them the same way.
/// </summary>
/// <remarks>
/// How a merge request names its task is a per-project convention, not a provider dialect, so it is a
/// pair of connection settings — a source and a pattern — rather than code in an adapter. A VCS
/// provider adds <see cref="Schema"/> to what it declares, and the ingestion builds a rule with
/// <see cref="RuleFrom"/> and applies it through <see cref="Core.Parsing.TaskKeyExtractor"/>. The
/// default reproduces the branch parser this replaced (<c>feature/PROJ-12</c> → <c>PROJ-12</c>), so a
/// connection that configures nothing keeps working exactly as before.
/// </remarks>
public static class TaskLinkSettings
{
    /// <summary>Settings-bag key naming which field the task key is read from (a <see cref="TaskKeySource"/> name).</summary>
    public const string SourceKey = "taskKeySource";

    /// <summary>Settings-bag key holding the regex applied to that field.</summary>
    public const string PatternKey = "taskKeyPattern";

    /// <summary>
    /// The default pattern: an uppercase tracker key like <c>PROJ-12</c> or <c>S3-42</c>, on a token
    /// boundary so <c>release/2024-01-ABC-15</c> does not yield <c>01-ABC</c>. The bracketed group is
    /// the key; the boundary assertions are not part of it.
    /// </summary>
    public const string DefaultPattern = @"(?<![A-Za-z0-9])([A-Z][A-Z0-9]*-\d+)(?![A-Za-z0-9])";

    /// <summary>The source assumed when a connection configures none.</summary>
    public const TaskKeySource DefaultSource = TaskKeySource.Branch;

    /// <summary>The two settings a provider declares so its connection form offers this configuration.</summary>
    public static IReadOnlyList<ProviderSettingSchema> Schema { get; } =
    [
        new(SourceKey,
            Label: "Task-key source",
            Description: "Where to read the task key from.",
            Kind: ProviderSettingKind.Enum,
            Options: ["branch", "title", "label"],
            Default: "branch"),
        new(PatternKey,
            Label: "Task-key pattern",
            Description: "Regex; the first capture group (or the whole match) is the task key.",
            Kind: ProviderSettingKind.Regex,
            Default: DefaultPattern)
    ];

    /// <summary>Reads the linking rule from a connection's settings, falling back to the defaults.</summary>
    /// <param name="settings">The connection's provider settings bag.</param>
    /// <returns>The source and pattern to apply.</returns>
    public static (TaskKeySource Source, string Pattern) RuleFrom(IReadOnlyDictionary<string, string> settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        var source = settings.TryGetValue(SourceKey, out var s)
                     && Enum.TryParse<TaskKeySource>(s, ignoreCase: true, out var parsed)
            ? parsed
            : DefaultSource;

        var pattern = settings.TryGetValue(PatternKey, out var p) && !string.IsNullOrWhiteSpace(p)
            ? p
            : DefaultPattern;

        return (source, pattern);
    }
}
