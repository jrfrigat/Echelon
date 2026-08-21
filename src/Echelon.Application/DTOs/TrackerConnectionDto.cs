namespace Echelon.Application.DTOs;

/// <summary>A configured tracker connection. Never carries the access token.</summary>
/// <param name="Id">The connection id.</param>
/// <param name="Name">The connection's name, unique across connections.</param>
/// <param name="TrackerType">The provider type, e.g. <c>yandextracker-poll</c>.</param>
/// <param name="ApiUrl">Where the tracker is reached.</param>
/// <param name="Settings">Provider-specific settings; secret ones are absent.</param>
public record TrackerConnectionDto(
    Guid Id,
    string Name,
    string TrackerType,
    string ApiUrl,
    IReadOnlyDictionary<string, string>? Settings = null);
