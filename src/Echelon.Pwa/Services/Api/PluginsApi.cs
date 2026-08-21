using Echelon.Application.DTOs;

namespace Echelon.Pwa.Services.Api;

/// <summary>What this build has installed.</summary>
/// <param name="http">The client this area talks over.</param>
public sealed class PluginsApi(HttpClient http) : ApiClient(http)
{
    /// <summary>The connectors, deploy strategies and action handlers this build has installed.</summary>
    public Task<List<PluginDto>> GetPluginsAsync(CancellationToken ct = default) =>
        GetAsync<List<PluginDto>>("api/plugins", ct);
}
