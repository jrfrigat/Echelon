using System.Text.Json.Serialization;

namespace Echelon.Providers.YandexTracker.Webhooks;

// Nullable throughout: System.Text.Json binds null into a non-nullable reference property when a
// field is absent, so a malformed payload would become a 500 on first dereference.
// YandexTrackerWebhookParser validates the required fields explicitly instead.
//
// Moved here from the ingress: this is Yandex.Tracker's wire shape, read only by its parser.

/// <summary>The Yandex.Tracker webhook payload, only the fields this app reads.</summary>
internal record YandexTrackerEventPayload(
    [property: JsonPropertyName("event")] string? Event,
    [property: JsonPropertyName("issue")] YandexTrackerIssue? Issue);

/// <summary>The issue an event concerns.</summary>
internal record YandexTrackerIssue(
    [property: JsonPropertyName("key")] string? Key,
    [property: JsonPropertyName("summary")] string? Summary,
    [property: JsonPropertyName("status")] YandexTrackerStatus? Status);

/// <summary>An issue's status.</summary>
internal record YandexTrackerStatus(
    [property: JsonPropertyName("key")] string? Key);
