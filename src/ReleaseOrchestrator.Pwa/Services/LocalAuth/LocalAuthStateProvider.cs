using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.JSInterop;

namespace ReleaseOrchestrator.Pwa.Services.LocalAuth;

/// <summary>
/// Supplies the app's authentication state from a JWT the <c>Local</c> provider issued, held in the
/// browser's localStorage.
/// </summary>
/// <remarks>
/// Used only when <c>Auth:Provider</c> is <c>Local</c>; AzureAd runs MSAL's own provider instead.
/// The token's claims (its <c>oid</c> and name) are read straight out of the JWT payload for the
/// client's own <c>AuthorizeView</c> checks - the server re-validates the signature on every call,
/// so nothing here is trusted for authorization, only for showing the right UI.
/// </remarks>
public sealed class LocalAuthStateProvider(IJSRuntime js) : AuthenticationStateProvider
{
    internal const string TokenStorageKey = "release-orchestrator.local-auth-token";

    private static readonly AuthenticationState Anonymous = new(new ClaimsPrincipal(new ClaimsIdentity()));

    /// <inheritdoc/>
    public override async Task<AuthenticationState> GetAuthenticationStateAsync()
    {
        var token = await js.InvokeAsync<string?>("localStorage.getItem", TokenStorageKey);
        if (string.IsNullOrWhiteSpace(token) || !TryReadClaims(token, out var claims))
            return Anonymous;

        // A token whose exp has passed is not a session. Treat it as anonymous so the router sends the
        // user to the login form, rather than showing the authenticated shell and letting every API
        // call fail with a 401 "session expired" the user cannot act on. The server is still the
        // authority on validity; this only stops a stale token from masquerading as a live session.
        if (IsExpired(claims))
        {
            await js.InvokeVoidAsync("localStorage.removeItem", TokenStorageKey);
            return Anonymous;
        }

        var identity = new ClaimsIdentity(claims, authenticationType: "Local", nameType: "name", roleType: "role");
        return new AuthenticationState(new ClaimsPrincipal(identity));
    }

    /// <summary>Re-reads the token and pushes the new state to the UI. Called after login and logout.</summary>
    public void NotifyChanged() => NotifyAuthenticationStateChanged(GetAuthenticationStateAsync());

    /// <summary>Reads the claims out of a JWT payload without validating the signature (the server does that).</summary>
    private static bool TryReadClaims(string token, out IEnumerable<Claim> claims)
    {
        claims = [];
        var parts = token.Split('.');
        if (parts.Length != 3) return false;

        try
        {
            var payload = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(Base64UrlDecode(parts[1]));
            if (payload is null) return false;

            var list = new List<Claim>();
            foreach (var (key, value) in payload)
            {
                if (value.ValueKind == JsonValueKind.Array)
                    list.AddRange(value.EnumerateArray().Select(v => new Claim(key, v.ToString())));
                else
                    list.Add(new Claim(key, value.ToString()));
            }

            claims = list;
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>True when the token's <c>exp</c> claim (Unix seconds) is in the past.</summary>
    private static bool IsExpired(IEnumerable<Claim> claims)
    {
        var exp = claims.FirstOrDefault(c => c.Type == "exp")?.Value;
        return long.TryParse(exp, out var seconds)
               && DateTimeOffset.FromUnixTimeSeconds(seconds) <= DateTimeOffset.UtcNow;
    }

    private static byte[] Base64UrlDecode(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded = (padded.Length % 4) switch { 2 => padded + "==", 3 => padded + "=", _ => padded };
        return Convert.FromBase64String(padded);
    }
}
