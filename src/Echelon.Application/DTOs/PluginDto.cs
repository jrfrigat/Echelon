using Echelon.Core.Enums;
using Echelon.Providers.Abstractions;

namespace Echelon.Application.DTOs;
/// <summary>An installed plugin, for the admin overview of what this build can talk to.</summary>
/// <remarks>
/// Echoed from the marker registrations the composition root added: this is each plugin's own
/// declaration, not a classification made here.
/// </remarks>
/// <param name="Category">Which axis the plugin extends.</param>
/// <param name="Key">Its registered key - provider type, strategy key or action type.</param>
/// <param name="Ingestion">Push or Poll for a connector; null for a deploy strategy or action handler.</param>
/// <param name="Description">The plugin's own one-line description; null when it declared none.</param>
public record PluginDto(
    PluginCategory Category,
    string Key,
    IngestionMode? Ingestion,
    string? Description);
