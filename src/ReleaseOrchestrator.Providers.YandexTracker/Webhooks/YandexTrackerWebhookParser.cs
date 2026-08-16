using System.Text.Json;
using ReleaseOrchestrator.Providers.Abstractions.Ingestion;

namespace ReleaseOrchestrator.Providers.YandexTracker.Webhooks;

/// <summary>
/// Turns a Yandex.Tracker issue webhook into normalized ingestion events.
/// </summary>
/// <remarks>
/// All of Yandex.Tracker's webhook knowledge, moved out of the ingress: the payload shape, the
/// <c>X-Tracker-Token</c> header, the <c>issue:*</c> event vocabulary, and the closed-status rule.
/// The provider keeps the four strings it already used in production (route <c>tracker</c>, config
/// section <c>Tracker</c>, source prefix <c>yandex</c>) so this move renames no route and no config
/// key and orphans no dedup rows - see <see cref="WebhookEndpointDescriptor"/>.
/// </remarks>
internal sealed class YandexTrackerWebhookParser : IWebhookParser
{
    /// <summary>The header Yandex.Tracker puts the shared secret in.</summary>
    private const string TokenHeader = "X-Tracker-Token";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <inheritdoc/>
    public WebhookEndpointDescriptor Descriptor { get; } = new(
        ProviderType: YandexTrackerProviderExtensions.WebhookProviderType,  // "yandextracker-webhook"
        // The three strings below intentionally differ from the key and from each other: this is the
        // spelling that has been in production, preserved so the split breaks no live webhook - the
        // route and dedup source prefix stay exactly as they were even as the key gains the -webhook suffix.
        RouteSegment: "tracker",
        SecretConfigSection: "Tracker",
        SourcePrefix: "yandex");

    /// <inheritdoc/>
    public bool Authenticate(WebhookRequest request, string? secret)
    {
        ArgumentNullException.ThrowIfNull(request);
        return WebhookSignatures.FixedTimeEquals(request.Header(TokenHeader), secret);
    }

    /// <inheritdoc/>
    public IReadOnlyList<IngestionEvent> Parse(WebhookRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var payload = Deserialize(request.Body.Span);

        if (payload.Issue?.Key is not { Length: > 0 } issueKey)
            throw new WebhookPayloadException("payload requires issue.key");

        // Yandex.Tracker sends no stable per-delivery id header, so these are not deduplicated yet:
        // ProviderEventId is empty, and the host reads that as "no dedup". Set it here if a
        // delivery-id header is confirmed against a live tracker.
        const string noDeliveryId = "";

        switch (payload.Event)
        {
            case "issue:created":
                return
                [
                    new TaskCreatedEvent(
                        ExternalId: issueKey,
                        Title: payload.Issue.Summary ?? string.Empty,
                        ProviderEventId: noDeliveryId)
                ];

            case "issue:updated":
            case "issue:statusUpdated":
                if (payload.Issue.Status?.Key is not { Length: > 0 } statusKey)
                    throw new WebhookPayloadException("status update requires issue.status.key");

                return
                [
                    new TaskStatusChangedEvent(
                        ExternalId: issueKey,
                        NewStatus: statusKey,
                        // The provider owns which statuses count as closed; the host stamps the time.
                        Closed: YandexTrackerStatusRules.IsClosed(statusKey),
                        ProviderEventId: noDeliveryId)
                ];

            default:
                // An event this app does not model. Understood, nothing to do - not an error.
                return [];
        }
    }

    private static YandexTrackerEventPayload Deserialize(ReadOnlySpan<byte> body)
    {
        try
        {
            return JsonSerializer.Deserialize<YandexTrackerEventPayload>(body, JsonOptions)
                   ?? throw new WebhookPayloadException("payload was empty");
        }
        catch (JsonException ex)
        {
            throw new WebhookPayloadException($"payload was not valid JSON: {ex.Message}");
        }
    }
}
