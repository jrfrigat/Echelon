using System.Text.Json.Serialization;

namespace ReleaseOrchestrator.Ingress.Webhooks.Models;

// Nullable throughout: System.Text.Json will happily bind null into a non-nullable
// reference property when a field is absent, turning a malformed payload into a 500.
public record YandexTrackerEventPayload(
    [property: JsonPropertyName("event")] string? Event,
    [property: JsonPropertyName("issue")] YandexTrackerIssue? Issue);

public record YandexTrackerIssue(
    [property: JsonPropertyName("key")] string? Key,
    [property: JsonPropertyName("summary")] string? Summary,
    [property: JsonPropertyName("status")] YandexTrackerStatus? Status);

public record YandexTrackerStatus(
    [property: JsonPropertyName("key")] string? Key);
