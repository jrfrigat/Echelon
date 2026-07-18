using System.Net.Http.Json;
using Microsoft.JSInterop;

namespace ReleaseOrchestrator.Pwa.Services.LocalAuth;

/// <summary>
/// Logs in and out against the <c>Local</c> provider's <c>/auth/login</c> endpoint, storing the
/// issued token in localStorage and telling the state provider to refresh.
/// </summary>
public sealed class LocalAuthService(HttpClient http, IJSRuntime js, LocalAuthStateProvider stateProvider)
{
    private record LoginRequest(string Username, string Password);
    private record LoginResponse(string Token, DateTime ExpiresAtUtc);

    /// <summary>Exchanges credentials for a token. Returns true on success.</summary>
    /// <param name="username">The username.</param>
    /// <param name="password">The password.</param>
    public async Task<bool> LoginAsync(string username, string password)
    {
        var response = await http.PostAsJsonAsync("auth/login", new LoginRequest(username, password));
        if (!response.IsSuccessStatusCode)
            return false;

        var body = await response.Content.ReadFromJsonAsync<LoginResponse>();
        if (body is null || string.IsNullOrWhiteSpace(body.Token))
            return false;

        await js.InvokeVoidAsync("localStorage.setItem", LocalAuthStateProvider.TokenStorageKey, body.Token);
        stateProvider.NotifyChanged();
        return true;
    }

    /// <summary>Clears the stored token and refreshes the state.</summary>
    public async Task LogoutAsync()
    {
        await js.InvokeVoidAsync("localStorage.removeItem", LocalAuthStateProvider.TokenStorageKey);
        stateProvider.NotifyChanged();
    }
}
