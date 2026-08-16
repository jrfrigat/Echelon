using ReleaseOrchestrator.Providers.Abstractions;
using ReleaseOrchestrator.Providers.Abstractions.Vcs;

namespace ReleaseOrchestrator.Providers.GitLab;

/// <summary>
/// GitLab reached by webhooks: it pushes merge-request events to the ingress, and this service only
/// calls the API to reconcile and to deploy.
/// </summary>
/// <remarks>
/// A thin wrapper over the shared <see cref="GitLabProviderAdapter"/>: the API behaviour is identical
/// to the poll type, so only the provider type and the settings differ. This type declares no extra
/// settings - the webhook shared secret is configuration of the ingress host, not of the connection.
/// </remarks>
internal sealed class GitLabWebhookProviderAdapter(GitLabProviderAdapter inner) : IVcsProviderAdapter
{
    /// <inheritdoc/>
    /// <remarks>Only the task-linking rule: the webhook secret is ingress configuration, not a connection field.</remarks>
    public IReadOnlyList<ProviderSettingSchema> SettingsSchema { get; } = [.. TaskLinkSettings.Schema];

    /// <inheritdoc/>
    public Task<IVcsProvider> ConnectAsync(VcsProviderContext context, CancellationToken ct) =>
        inner.ConnectAsync(context, ct);
}

/// <summary>
/// GitLab reached by polling: the service lists open merge requests on an interval, for an install
/// that cannot deliver webhooks.
/// </summary>
/// <remarks>
/// The sibling of <see cref="GitLabWebhookProviderAdapter"/> over the same shared connect logic. It
/// declares the one setting polling needs - the interval - under the neutral
/// <see cref="VcsPollSettings.IntervalKey"/>, so the poller reads it without knowing this is GitLab.
/// </remarks>
internal sealed class GitLabPollProviderAdapter(GitLabProviderAdapter inner) : IVcsProviderAdapter
{
    /// <inheritdoc/>
    /// <remarks>The poll interval plus the task-linking rule every GitLab connection carries.</remarks>
    public IReadOnlyList<ProviderSettingSchema> SettingsSchema { get; } =
    [
        new(VcsPollSettings.IntervalKey,
            Label: "Poll interval (seconds)",
            Description: "How often to list this connection's open merge requests.",
            Kind: ProviderSettingKind.Int,
            Default: VcsPollSettings.DefaultIntervalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Min: VcsPollSettings.MinIntervalSeconds,
            Max: VcsPollSettings.MaxIntervalSeconds),
        .. TaskLinkSettings.Schema
    ];

    /// <inheritdoc/>
    public Task<IVcsProvider> ConnectAsync(VcsProviderContext context, CancellationToken ct) =>
        inner.ConnectAsync(context, ct);
}
