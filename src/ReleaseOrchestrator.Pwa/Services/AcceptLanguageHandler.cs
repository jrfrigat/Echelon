using System.Globalization;
using System.Net.Http.Headers;

namespace ReleaseOrchestrator.Pwa.Services;

/// <summary>
/// Tells the API which language to answer in.
///
/// Without this the header the browser attaches on its own wins, which is the browser's language,
/// not the one picked in the app — so a user on an English browser who switches to Russian would
/// still get English validation errors back. Set per request rather than on the client's default
/// headers because the culture can change after the client is built.
/// </summary>
public sealed class AcceptLanguageHandler : DelegatingHandler
{
    /// <inheritdoc />
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.Headers.AcceptLanguage.Clear();
        request.Headers.AcceptLanguage.Add(
            new StringWithQualityHeaderValue(CultureInfo.CurrentUICulture.TwoLetterISOLanguageName));

        return base.SendAsync(request, cancellationToken);
    }
}
