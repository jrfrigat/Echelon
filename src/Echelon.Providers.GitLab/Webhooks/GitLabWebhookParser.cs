using System.Text.Json;
using Echelon.Core.Enums;
using Echelon.Core.Parsing;
using Echelon.Providers.Abstractions.Ingestion;

namespace Echelon.Providers.GitLab.Webhooks;

/// <summary>
/// Turns a GitLab merge-request webhook into normalized ingestion events.
/// </summary>
/// <remarks>
/// This is the whole of GitLab's webhook knowledge, moved out of the ingress: the payload shape,
/// the <c>X-Gitlab-Token</c> and <c>X-Gitlab-Event-UUID</c> header names, the
/// <c>merge_request</c> event filter, the state dictionary, and where GitLab puts labels. The host
/// keeps only what every provider shares - the route, the secret lookup, and sending the events.
/// </remarks>
internal sealed class GitLabWebhookParser : IWebhookParser
{
    /// <summary>The header GitLab puts the shared secret in.</summary>
    private const string TokenHeader = "X-Gitlab-Token";

    /// <summary>
    /// The header GitLab stamps with a per-delivery id, reused on a retry. It is the dedup key: a
    /// retried delivery is dropped, a genuine re-send (a label change) carries a new id and passes.
    /// Absent on older GitLab, in which case the event is simply not deduplicated.
    /// </summary>
    private const string DeliveryHeader = "X-Gitlab-Event-UUID";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <inheritdoc/>
    /// <remarks>
    /// Keyed by the webhook provider type, since only that type has a webhook. The route, config
    /// section and source prefix stay <c>gitlab</c>: they are cosmetic provenance, and keeping them
    /// stable leaves webhook URLs and secret config keys unchanged by the split.
    /// </remarks>
    public WebhookEndpointDescriptor Descriptor { get; } = new(
        ProviderType: GitLabProviderExtensions.WebhookProviderType,  // "gitlab-webhook"
        RouteSegment: "gitlab",
        SecretConfigSection: "GitLab",
        SourcePrefix: "gitlab");

    /// <inheritdoc/>
    public bool Authenticate(WebhookRequest request, string? secret)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Identical to the check the ingress ran, now owned by the provider that knows the header:
        // constant-time, fails closed on a missing secret or a missing header.
        return WebhookSignatures.FixedTimeEquals(request.Header(TokenHeader), secret);
    }

    /// <inheritdoc/>
    public IReadOnlyList<IngestionEvent> Parse(WebhookRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var payload = Deserialize(request.Body.Span);

        // Not a merge-request event: understood, nothing to do. Empty (host answers 200), never an
        // error - GitLab sends many event kinds to one webhook and must not be told each is a failure.
        if (payload.ObjectKind != "merge_request")
            return [];

        var attributes = payload.ObjectAttributes;
        if (payload.Project?.PathWithNamespace is not { Length: > 0 } repositoryExternalId
            || attributes?.Iid is not { } iid
            || attributes.State is not { Length: > 0 } state)
        {
            throw new WebhookPayloadException(
                "payload requires project.path_with_namespace, object_attributes.iid and object_attributes.state");
        }

        // GitLab's own state dictionary, shared with the poll path so both arrival routes cannot
        // disagree about what "merged" means. A state this app does not model yields nothing.
        var status = GitLabMergeRequestState.FromState(state);
        if (status is null)
            return [];

        var externalMrId = iid.ToString();
        var deliveryId = request.Header(DeliveryHeader) ?? string.Empty;
        var labels = ExtractLabels(payload);

        if (status == MergeRequestStatus.Opened)
        {
            // Emitted for every open-state event, not only the first: GitLab re-sends on label
            // changes, pushes and reopens, and the consumer upserts, so a reopened MR rejoins the
            // plan and a newly labelled one enters it.
            return
            [
                new MrOpenedEvent(
                    RepositoryExternalId: repositoryExternalId,
                    ExternalMrId: externalMrId,
                    SourceBranch: attributes.SourceBranch ?? string.Empty,
                    TargetBranch: attributes.TargetBranch ?? string.Empty,
                    // The task key is resolved downstream from the connection's rule, not here: the
                    // parser carries the raw candidates (branch, title, labels) instead.
                    Title: attributes.Title,
                    Labels: labels,
                    ProviderEventId: deliveryId)
            ];
        }

        return
        [
            new MrStatusChangedEvent(
                RepositoryExternalId: repositoryExternalId,
                ExternalMrId: externalMrId,
                NewStatus: status.Value,
                Labels: labels,
                ProviderEventId: deliveryId)
        ];
    }

    private static GitLabMrPayload Deserialize(ReadOnlySpan<byte> body)
    {
        try
        {
            return JsonSerializer.Deserialize<GitLabMrPayload>(body, JsonOptions)
                   ?? throw new WebhookPayloadException("payload was empty");
        }
        catch (JsonException ex)
        {
            throw new WebhookPayloadException($"payload was not valid JSON: {ex.Message}");
        }
    }

    /// <summary>
    /// Reads the labels off a payload and normalizes them to the one comparison form.
    /// </summary>
    /// <remarks>
    /// GitLab reports labels at the top level and, for some events, only under
    /// <c>object_attributes</c>; this reads whichever is present. It then runs them through
    /// <see cref="LabelSet.Normalize"/> - the same form the readiness gate compares against - rather
    /// than the ingress's old ad-hoc trim/dedup, which lower-cased differently and did not sort, so
    /// the webhook and the gate could disagree about whether two spellings were one label.
    /// </remarks>
    private static IReadOnlyList<string> ExtractLabels(GitLabMrPayload payload) =>
        LabelSet.Normalize(
            (payload.Labels ?? payload.ObjectAttributes?.Labels ?? [])
            // l?.Title, not l.Title: a JSON null element in the array binds to a null label, which
            // would otherwise NullReference on an authenticated payload.
            .Select(l => l?.Title));
}
