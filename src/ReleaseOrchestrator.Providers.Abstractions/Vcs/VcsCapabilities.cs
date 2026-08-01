namespace ReleaseOrchestrator.Providers.Abstractions.Vcs;

/// <summary>
/// What one VCS connection can actually do.
/// </summary>
/// <remarks>
/// <para>
/// Per connection, not per provider type. "GitLab supports X" is not a fact — a self-hosted
/// GitLab is whatever version its owner last upgraded to, and two connections to two installs
/// answer differently. Atlantis reached the same shape with
/// <c>SupportsSingleFileDownload(repo)</c>: the predicate takes the repository because a global
/// answer would be a lie.
/// </para>
/// <para>
/// This object answers "can this install do it". The other two questions have their own
/// mechanisms: "can this provider do it at all" is an optional interface plus an <c>is</c> check,
/// and "is the server new enough" is settled once at connect time and cached — see
/// <see cref="ServerVersion"/>.
/// </para>
/// </remarks>
public sealed record VcsCapabilities
{
    /// <summary>
    /// The server version reported at connect time, or <c>null</c> when it could not be detected.
    /// </summary>
    /// <remarks>
    /// Null is not an error: an install can be behind a proxy that hides <c>/version</c>, and
    /// refusing to work at all would be worse than proceeding with the conservative defaults the
    /// adapter picks. Callers must treat null as "unknown", not as "old".
    /// </remarks>
    public string? ServerVersion { get; init; }

    /// <summary>
    /// Whether the merge-request API reports labels on this install.
    /// </summary>
    /// <remarks>
    /// Distinguishes an empty <see cref="VcsMergeRequest.Labels"/> meaning "no labels" from one
    /// meaning "this provider cannot tell you". The difference matters because a per-environment
    /// readiness rule is evaluated against the stored label set, so reading "cannot say" as "the
    /// labels were removed" would overwrite that set and hold a merge request out of an environment
    /// it qualifies for — or, on a rule that gates on absence, let one through.
    /// </remarks>
    public bool SupportsMergeRequestLabels { get; init; }

    /// <summary>
    /// Whether this install reports the repository's branches.
    /// </summary>
    /// <remarks>
    /// Same distinction as <see cref="SupportsMergeRequestLabels"/>: an empty branch list from a
    /// provider that cannot list them must not be read as "the task has no unlanded work", which would
    /// silently drop the blocker the branch was supposed to raise.
    /// </remarks>
    public bool SupportsBranches { get; init; }

    /// <summary>Capabilities of a provider that has told us nothing: assume the minimum.</summary>
    public static VcsCapabilities None { get; } = new();
}
