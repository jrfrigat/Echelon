using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Echelon.Infrastructure.Auth;

namespace Echelon.Web.Auth;

/// <summary>
/// Issues the JWTs the <c>Local</c> provider hands out at login, and describes how they are
/// validated on the way back in.
/// </summary>
/// <remarks>
/// One symmetric key both signs and validates, so the token never leaves this service's trust
/// boundary. The token carries the two claims the rest of the app reads: <c>oid</c> (a GUID, which
/// permissions key on) and the display name.
/// </remarks>
public sealed class LocalTokenService(IOptions<LocalAuthenticationOptions> options, TimeProvider clock)
{
    private readonly LocalAuthenticationOptions _options = options.Value;

    /// <summary>Signs a token for the given user and returns it with its expiry.</summary>
    /// <param name="objectId">The user's object id, stamped as <c>oid</c>.</param>
    /// <param name="displayName">The user's display name.</param>
    /// <returns>The serialized JWT and the instant it expires.</returns>
    public (string Token, DateTime ExpiresAtUtc) Issue(string objectId, string displayName)
    {
        // The injected clock, like everything else that stamps a time here: a token's lifetime is
        // worth being able to test at a boundary rather than by waiting for one.
        var now = clock.GetUtcNow().UtcDateTime;
        var expires = now.AddMinutes(_options.TokenLifetimeMinutes);

        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = _options.Issuer,
            Audience = _options.Issuer,
            IssuedAt = now,
            NotBefore = now,
            Expires = expires,
            Claims = new Dictionary<string, object>
            {
                [UserIdentifier.ObjectIdClaimType] = objectId,
                ["name"] = displayName
            },
            SigningCredentials = new SigningCredentials(SigningKey(_options.SigningKey), SecurityAlgorithms.HmacSha256)
        };

        return (new JsonWebTokenHandler().CreateToken(descriptor), expires);
    }

    /// <summary>The parameters the JwtBearer handler validates incoming Local tokens with.</summary>
    public static TokenValidationParameters ValidationParameters(LocalAuthenticationOptions options) => new()
    {
        ValidateIssuer = true,
        ValidIssuer = options.Issuer,
        ValidateAudience = true,
        ValidAudience = options.Issuer,
        ValidateIssuerSigningKey = true,
        IssuerSigningKey = SigningKey(options.SigningKey),
        ValidateLifetime = true,
        // The display name lives in "name"; permissions read "oid" directly via UserIdentifier.
        NameClaimType = "name"
    };

    private static SymmetricSecurityKey SigningKey(string key) =>
        new(Encoding.UTF8.GetBytes(key));
}
