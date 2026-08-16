using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace ReleaseOrchestrator.Web.Auth;

/// <summary>
/// The login endpoint for the <c>Local</c> provider. Mapped only when that provider is selected.
/// </summary>
public static class LocalAuthEndpoints
{
    /// <summary>A login request body.</summary>
    /// <param name="Username">The username.</param>
    /// <param name="Password">The password.</param>
    public record LoginRequest(string Username, string Password);

    /// <summary>A successful login response.</summary>
    /// <param name="Token">The bearer token to send on subsequent requests.</param>
    /// <param name="ExpiresAtUtc">When the token expires.</param>
    public record LoginResponse(string Token, DateTime ExpiresAtUtc);

    /// <summary>Maps <c>POST /auth/login</c>. Anonymous - it is how a caller becomes authenticated.</summary>
    /// <param name="endpoints">The endpoint route builder.</param>
    /// <returns>The same builder, for chaining.</returns>
    public static IEndpointRouteBuilder MapLocalAuthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapPost("/auth/login", (
                [FromBody] LoginRequest request,
                IOptions<LocalAuthenticationOptions> options,
                LocalTokenService tokens) =>
            {
                var config = options.Value;

                // Config store: one admin, its credentials in configuration. Both sides are compared
                // in constant time so a wrong username cannot be told from a wrong password by timing.
                var ok = FixedTimeEquals(request.Username, config.Username)
                         & FixedTimeEquals(request.Password, config.Password);

                if (!ok)
                    return Results.Unauthorized();

                var (token, expiresAt) = tokens.Issue(config.AdminObjectId, config.Username);
                return Results.Ok(new LoginResponse(token, expiresAt));
            })
            .AllowAnonymous()
            .WithName("LocalLogin");

        return endpoints;
    }

    /// <summary>Length-independent constant-time compare, via a fixed-size hash of each side.</summary>
    private static bool FixedTimeEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(a)),
            SHA256.HashData(Encoding.UTF8.GetBytes(b)));
}
