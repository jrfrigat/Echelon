using System.Net;

namespace Echelon.Pwa.Services.Api;

/// <summary>
/// Carries the server's own explanation of a failure so the UI can show it.
/// </summary>
/// <remarks>
/// The API answers with ProblemDetails or an <c>{ error }</c> body naming exactly what was wrong, and
/// it answers in the caller's language. Throwing that sentence rather than returning false is what
/// lets a page show the reason instead of a generic "failed to save".
/// </remarks>
/// <param name="message">The server's explanation.</param>
/// <param name="statusCode">The status it came with.</param>
public class ApiException(string message, HttpStatusCode statusCode) : Exception(message)
{
    /// <summary>The status the server answered with.</summary>
    public HttpStatusCode StatusCode { get; } = statusCode;
}
