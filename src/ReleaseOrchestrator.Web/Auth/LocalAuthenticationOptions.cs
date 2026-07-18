namespace ReleaseOrchestrator.Web.Auth;

/// <summary>
/// Settings for the <c>Local</c> authentication provider — a self-contained login that issues the
/// service's own JWTs, for a deployment with no external identity provider.
/// </summary>
/// <remarks>
/// Bound from the <c>Auth:Local</c> configuration section. The <c>Store</c> decides where user
/// credentials live; Phase 1 ships <c>Config</c> (a single admin from these settings). The token is
/// signed with <see cref="SigningKey"/> and validated by the same key on the way back in, so the
/// key is a shared secret that must be set in any real deployment.
/// </remarks>
public sealed class LocalAuthenticationOptions
{
    /// <summary>Where credentials are stored: <c>Config</c> (this section), or later a database store.</summary>
    public string Store { get; set; } = "Config";

    /// <summary>The admin username, for the <c>Config</c> store.</summary>
    public string Username { get; set; } = "admin";

    /// <summary>The admin password, for the <c>Config</c> store. No default — a blank one is refused at startup.</summary>
    public string Password { get; set; } = string.Empty;

    /// <summary>
    /// The object id stamped into the token as <c>oid</c>. Permissions key on this, so it must be a
    /// GUID; it is granted every permission automatically (see AuthenticationSetup) so the local
    /// admin can actually use the app.
    /// </summary>
    public string AdminObjectId { get; set; } = "00000000-0000-0000-0000-0000000000ad";

    /// <summary>The HMAC signing key for issued tokens. At least 32 chars in a real deployment.</summary>
    public string SigningKey { get; set; } = string.Empty;

    /// <summary>The token issuer and audience — arbitrary but must match between issue and validation.</summary>
    public string Issuer { get; set; } = "release-orchestrator";

    /// <summary>How long an issued token is valid, in minutes.</summary>
    public int TokenLifetimeMinutes { get; set; } = 480;
}
