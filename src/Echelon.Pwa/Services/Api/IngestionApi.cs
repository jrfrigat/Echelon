using Echelon.Application.DTOs;

namespace Echelon.Pwa.Services.Api;

/// <summary>What the ingestion side of the server is doing.</summary>
/// <param name="http">The client this area talks over.</param>
public sealed class IngestionApi(HttpClient http) : ApiClient(http)
{
    /// <summary>The workers, what has arrived, and the last poll of each connection.</summary>
    /// <param name="ct">Cancellation token.</param>
    public Task<IngestionStatusDto> GetStatusAsync(CancellationToken ct = default) =>
        GetAsync<IngestionStatusDto>("api/ingestion", ct);
}
