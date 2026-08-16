using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.JSInterop;

namespace Echelon.Pwa.Services.LocalAuth;

/// <summary>
/// Logs in and out against the <c>Local</c> provider's <c>/auth/login</c> endpoint, storing the
/// issued token in localStorage and telling the state provider to refresh.
/// </summary>
public sealed class LocalAuthService(HttpClient http, IJSRuntime js, LocalAuthStateProvider stateProvider)
{
    /// <summary>Exchanges credentials for a token. Returns true on success.</summary>
    /// <param name="username">The username.</param>
    /// <param name="password">The password.</param>
    public async Task<bool> LoginAsync(string username, string password)
    {
        // The body is written as a JsonObject rather than a typed record: WASM trimming strips the
        // reflection metadata a record needs to deserialize, so a typed read silently yields an empty
        // token even on a 200. JsonDocument reads by name with no reflection.
        var request = new Dictionary<string, string> { ["username"] = username, ["password"] = password };
        var response = await http.PostAsJsonAsync("auth/login", request);
        if (!response.IsSuccessStatusCode)
            return false;

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        if (!doc.RootElement.TryGetProperty("token", out var tokenElement))
            return false;

        var token = tokenElement.GetString();
        if (string.IsNullOrWhiteSpace(token))
            return false;

        await js.InvokeVoidAsync("localStorage.setItem", LocalAuthStateProvider.TokenStorageKey, token);
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
