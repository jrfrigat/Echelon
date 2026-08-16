using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Identity.Web;
using ReleaseOrchestrator.Infrastructure.Auth;

namespace ReleaseOrchestrator.Web.Auth;

/// <summary>
/// Registers the authentication scheme the deployment selected with <c>Auth:Provider</c>.
/// </summary>
/// <remarks>
/// The provider is a deploy-time choice, fixed by configuration and never switched at runtime, so
/// exactly one scheme is wired at startup. Whichever it is, the principal it produces flows into the
/// same permission pipeline (<see cref="PermissionClaimsTransformation"/>), which keys on the
/// <c>oid</c> claim - so every provider's token must carry a stable <c>oid</c>.
///
/// <list type="bullet">
///   <item><c>AzureAd</c> - Microsoft Entra ID via Microsoft.Identity.Web. The default, and the
///   original behaviour.</item>
///   <item><c>Oidc</c> - any OpenID Connect provider (Keycloak, Auth0, ...), validated as a plain
///   JWT bearer against its authority. The provider must issue an <c>oid</c> claim for permissions
///   to resolve.</item>
///   <item><c>Local</c> - no external identity provider: the service issues its own tokens through
///   <c>/auth/login</c>. See <see cref="LocalAuthenticationOptions"/>.</item>
/// </list>
/// </remarks>
public static class AuthenticationSetup
{
    /// <summary>Registers the configured authentication provider.</summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">Application configuration.</param>
    /// <returns>The same collection, for chaining.</returns>
    public static IServiceCollection AddConfiguredAuthentication(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var provider = (configuration["Auth:Provider"] ?? "AzureAd").Trim();

        switch (provider.ToLowerInvariant())
        {
            case "azuread":
                services.AddMicrosoftIdentityWebApiAuthentication(configuration, "AD");
                break;

            case "oidc":
                AddOidc(services, configuration);
                break;

            case "local":
                AddLocal(services, configuration);
                break;

            default:
                throw new InvalidOperationException(
                    $"Auth:Provider is '{provider}'. Set it to AzureAd, Oidc or Local.");
        }

        return services;
    }

    private static void AddOidc(IServiceCollection services, IConfiguration configuration)
    {
        var authority = Required(configuration, "Auth:Oidc:Authority");
        var audience = configuration["Auth:Oidc:Audience"];

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.Authority = authority;
                // A missing audience turns audience validation off, for providers that scope by
                // resource indicator rather than a fixed aud.
                options.Audience = audience;
                options.TokenValidationParameters.ValidateAudience = !string.IsNullOrWhiteSpace(audience);
                options.TokenValidationParameters.NameClaimType = "name";
            });
    }

    private static void AddLocal(IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<LocalAuthenticationOptions>()
            .Bind(configuration.GetSection("Auth:Local"))
            .Validate(o => !string.IsNullOrWhiteSpace(o.Password),
                "Auth:Local:Password is required when Auth:Provider is Local.")
            .Validate(o => !string.IsNullOrWhiteSpace(o.SigningKey) && o.SigningKey.Length >= 32,
                "Auth:Local:SigningKey is required and must be at least 32 characters.")
            .Validate(o => Guid.TryParse(o.AdminObjectId, out _),
                "Auth:Local:AdminObjectId must be a GUID.")
            .ValidateOnStart();

        services.AddSingleton<LocalTokenService>();

        var localOptions = new LocalAuthenticationOptions();
        configuration.GetSection("Auth:Local").Bind(localOptions);

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options => options.TokenValidationParameters = LocalTokenService.ValidationParameters(localOptions));

        // The local admin has no database grants and there is no other way to seed them here, so it
        // is made a bootstrap admin: it logs in and immediately holds every permission. This is the
        // same escape hatch Entra deployments use, and it is honest - a Local deployment is a single
        // operator who owns the connection string anyway.
        services.PostConfigure<PermissionBootstrapOptions>(bootstrap =>
            bootstrap.BootstrapAdminObjectIds = [.. bootstrap.BootstrapAdminObjectIds, localOptions.AdminObjectId]);
    }

    private static string Required(IConfiguration configuration, string key) =>
        configuration[key] is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException(
                $"{key} is not configured. Set {key.Replace(':', '_').Replace("_", "__")} in the environment.");
}
