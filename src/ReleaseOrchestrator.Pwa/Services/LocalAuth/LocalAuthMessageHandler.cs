using System.Net.Http.Headers;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace ReleaseOrchestrator.Pwa.Services.LocalAuth;

/// <summary>
/// Attaches the stored <c>Local</c> bearer token to outgoing API calls to the app's own origin.
/// </summary>
/// <remarks>
/// The <c>Local</c> counterpart of MSAL's <c>BaseAddressAuthorizationMessageHandler</c>: without a
/// handler putting the token on the request, every call hits the API's authenticated fallback and
/// 401s. Like MSAL's handler, it attaches the token ONLY for requests to the app's base address, so
/// the bearer token cannot ride along on a request to a third-party URL if this client is ever reused
/// for one. Reads the token fresh per request so a logout takes effect immediately.
/// </remarks>
public sealed class LocalAuthMessageHandler(IJSRuntime js, NavigationManager navigation) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var baseUri = new Uri(navigation.BaseUri);
        if (request.RequestUri is { } uri && baseUri.IsBaseOf(uri))
        {
            var token = await js.InvokeAsync<string?>("localStorage.getItem", cancellationToken, LocalAuthStateProvider.TokenStorageKey);
            if (!string.IsNullOrWhiteSpace(token))
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return await base.SendAsync(request, cancellationToken);
    }
}
