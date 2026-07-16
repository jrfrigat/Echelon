using System.Text.Json.Serialization;

namespace ReleaseOrchestrator.Ingress.Webhooks.Models;

public record YandexTrackerEventPayload(
    [property: JsonPropertyName("event")] string Event,
    [property: JsonPropertyName("issue")] YandexTrackerIssue Issue);

public record YandexTrackerIssue(
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("summary")] string Summary,
    [property: JsonPropertyName("status")] YandexTrackerStatus Status);

public record YandexTrackerStatus(
    [property: JsonPropertyName("key")] string Key);
