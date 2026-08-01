using ReleaseOrchestrator.Providers.Abstractions.Vcs;

namespace ReleaseOrchestrator.Providers.GitLab;

/// <summary>
/// Builds a <see cref="IVcsProvider"/> for a GitLab connection.
/// </summary>
/// <remarks>
/// The install is asked for its version once here, and the answer decides the capabilities the
/// resulting provider reports. This is the "is the server new enough" question being settled at
/// connect time rather than at each call site — the shape Renovate's <c>initPlatform</c> arrived at.
/// </remarks>
internal sealed class GitLabProviderAdapter(
    HttpClient http,
    GitLabVersionDetector versionDetector) : IVcsProviderAdapter
{
    /// <summary>
    /// The version from which the merge-request API reports labels.
    /// </summary>
    /// <remarks>
    /// Every GitLab this product would plausibly meet is newer than this. It is stated anyway
    /// because the alternative is an unconditional <c>true</c>, which is a claim about all
    /// installs that nothing checks — and an install old enough to matter would then report
    /// "no labels" rather than "cannot say", quietly dropping merge requests out of the plan.
    /// </remarks>
    private const int LabelsMinimumMajor = 9;
    private const int LabelsMinimumMinor = 0;

    /// <inheritdoc/>
    public async Task<IVcsProvider> ConnectAsync(VcsProviderContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);

        var version = await versionDetector.DetectAsync(http, context.ApiUrl, ct).ConfigureAwait(false);

        var capabilities = new VcsCapabilities
        {
            ServerVersion = version?.Raw,
            // Unknown version keeps the capability on: this adapter only supports GitLab, and
            // every supported GitLab reports labels. Turning it off on an undetectable version
            // would make an unreachable /version endpoint look like "the deploy label was
            // removed" and drop merge requests from the plan.
            SupportsMergeRequestLabels = version is null || version.Value.IsAtLeast(LabelsMinimumMajor, LabelsMinimumMinor),
            // The branches endpoint predates every GitLab this adapter supports, so it is always on —
            // and, as with labels, an unknown version must not read as "this repository has no work".
            SupportsBranches = true
        };

        return new GitLabProvider(http, context, capabilities);
    }
}
