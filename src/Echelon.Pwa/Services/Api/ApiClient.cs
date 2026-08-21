using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Echelon.Pwa.Services.Api;

/// <summary>
/// What every API client shares: the settings, the failure handling, and the query-string builder.
/// </summary>
/// <remarks>
/// <para>
/// One client per area of the API rather than one class for all of it - each screen takes the one it
/// needs, and a change to tasks cannot touch the file that talks about permissions. The plumbing lives
/// here so it exists once.
/// </para>
/// <para>
/// Failures surface as <see cref="ApiException"/> carrying the server's own message. These methods
/// used to return null or false on error, discarding the reason: the API answers with ProblemDetails
/// or an <c>{ error }</c> body saying exactly what was wrong, and the user was shown a generic
/// "failed to save" instead.
/// </para>
/// </remarks>
/// <param name="http">The client this area talks over, already carrying the auth and language handlers.</param>
public abstract class ApiClient(HttpClient http)
{
    /// <summary>Web defaults plus enum names, matching what the API writes and accepts.</summary>
    /// <remarks>
    /// The API registers <c>JsonStringEnumConverter</c>, so an enum arrives as its name and has to be
    /// sent as one. Reads and writes both go through this: without it an enum-typed field would fail to
    /// deserialize on the way in and arrive as a number on the way out.
    /// </remarks>
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>The HTTP client for this area.</summary>
    protected HttpClient Http { get; } = http;

    /// <summary>Builds a query string, dropping every parameter that is null or blank.</summary>
    /// <param name="parts">Name/value pairs, in the order they should appear.</param>
    /// <remarks>
    /// An empty parameter is not the same as an absent one to a model binder, and the hand-built
    /// strings this replaced sent <c>status=</c> and <c>search=</c> on every call.
    /// </remarks>
    protected static string Query(params (string Key, object? Value)[] parts) =>
        string.Join("&", parts
            .Where(p => p.Value?.ToString() is { Length: > 0 })
            .Select(p => $"{p.Key}={Uri.EscapeDataString(p.Value!.ToString()!)}"));

    /// <summary>Reads a resource that must exist.</summary>
    /// <typeparam name="T">The response shape.</typeparam>
    /// <param name="url">Relative URL.</param>
    /// <param name="ct">Cancellation token.</param>
    protected async Task<T> GetAsync<T>(string url, CancellationToken ct)
    {
        var response = await Http.GetAsync(url, ct);
        await EnsureSuccessAsync(response, ct);

        return await response.Content.ReadFromJsonAsync<T>(Json, ct)
               ?? throw new ApiException("The server returned an empty response.", response.StatusCode);
    }

    /// <summary>For endpoints where absence is a normal answer rather than a failure.</summary>
    /// <typeparam name="T">The response shape.</typeparam>
    /// <param name="url">Relative URL.</param>
    /// <param name="ct">Cancellation token.</param>
    protected async Task<T?> GetOrNullAsync<T>(string url, CancellationToken ct)
    {
        var response = await Http.GetAsync(url, ct);
        if (response.StatusCode == HttpStatusCode.NotFound) return default;

        await EnsureSuccessAsync(response, ct);
        return await response.Content.ReadFromJsonAsync<T>(Json, ct);
    }

    /// <summary>Sends a request and reads its body.</summary>
    /// <typeparam name="T">The response shape.</typeparam>
    /// <param name="send">The call to make.</param>
    /// <param name="ct">Cancellation token.</param>
    protected async Task<T> SendAsync<T>(Func<Task<HttpResponseMessage>> send, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(send);

        var response = await send();
        await EnsureSuccessAsync(response, ct);

        return await response.Content.ReadFromJsonAsync<T>(Json, ct)
               ?? throw new ApiException("The server returned an empty response.", response.StatusCode);
    }

    /// <summary>Sends a request that answers with nothing worth reading.</summary>
    /// <param name="send">The call to make.</param>
    /// <param name="ct">Cancellation token.</param>
    protected async Task SendAsync(Func<Task<HttpResponseMessage>> send, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(send);

        await EnsureSuccessAsync(await send(), ct);
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;

        throw new ApiException(await ReadErrorAsync(response, ct), response.StatusCode);
    }

    /// <summary>
    /// The API reports failures two ways: ProblemDetails from the domain exception handler, and a
    /// plain <c>{ error }</c> body from controller-level validation. Try both before falling back to
    /// the status code.
    /// </summary>
    /// <param name="response">The failed response.</param>
    /// <param name="ct">Cancellation token.</param>
    protected static async Task<string> ReadErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(response);

        try
        {
            var body = await response.Content.ReadFromJsonAsync<ErrorBody>(Json, ct);
            if (!string.IsNullOrWhiteSpace(body?.Detail)) return body.Detail;
            if (!string.IsNullOrWhiteSpace(body?.Error)) return body.Error;
            if (!string.IsNullOrWhiteSpace(body?.Title)) return body.Title;
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException or HttpRequestException)
        {
            // A body that is not JSON says nothing useful; fall through to the status code.
        }

        return $"{(int)response.StatusCode} {response.ReasonPhrase}";
    }

    /// <summary>Both error shapes the API can answer with, read as one.</summary>
    private sealed record ErrorBody(string? Detail, string? Error, string? Title);
}
