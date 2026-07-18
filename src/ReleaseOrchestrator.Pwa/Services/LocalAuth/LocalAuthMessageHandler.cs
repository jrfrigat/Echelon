using System.Net.Http.Headers;
using Microsoft.JSInterop;

namespace ReleaseOrchestrator.Pwa.Services.LocalAuth;

/// <summary>
/// Attaches the stored <c>Local</c> bearer token to outgoing API calls.
/// </summary>
/// <remarks>
/// The <c>Local</c> counterpart of MSAL's <c>BaseAddressAuthorizationMessageHandler</c>: without a
/// handler putting the token on the request, every call hits the API's authenticated fallback and
/// 401s. Reads the token fresh per request so a logout takes effect immediately.
/// </remarks>
public sealed class LocalAuthMessageHandler(IJSRuntime js) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var token = await js.InvokeAsync<string?>("localStorage.getItem", cancellationToken, LocalAuthStateProvider.TokenStorageKey);
        if (!string.IsNullOrWhiteSpace(token))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        return await base.SendAsync(request, cancellationToken);
    }
}
