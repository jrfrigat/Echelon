using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ReleaseOrchestrator.Infrastructure.Persistence;

namespace ReleaseOrchestrator.Infrastructure.Auth;

/// <summary>
/// Wires up ASP.NET Core Data Protection, which encrypts the VCS and tracker access tokens.
/// </summary>
/// <remarks>
/// <para>
/// The key ring is persisted to the same database as the tokens it encrypts. That is fine only if
/// the keys themselves are encrypted at rest with something the database does not hold: otherwise
/// anyone with a dump — a backup, a restore to a test box, a DBA, an open port — has both the
/// ciphertext and the key, and the encryption buys nothing at all. It is the digital equivalent of
/// taping the key to the lock.
/// </para>
/// <para>
/// So a certificate is required outside Development. An operator who accepts the risk can say so
/// explicitly with <c>DataProtection:AllowUnprotectedKeys</c>; what they cannot do is get there by
/// not noticing.
/// </para>
/// </remarks>
public static class DataProtectionSetup
{
    /// <summary>Configuration section carrying the key-protection settings.</summary>
    public const string SectionName = "DataProtection";

    /// <summary>
    /// Registers Data Protection with key encryption resolved from configuration.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="config">Application configuration.</param>
    /// <returns>The same collection, for chaining.</returns>
    /// <exception cref="InvalidOperationException">
    /// Running outside Development with neither a certificate nor an explicit opt-out.
    /// </exception>
    public static IServiceCollection AddTokenProtection(this IServiceCollection services, IConfiguration config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var builder = services.AddDataProtection()
            .SetApplicationName("ReleaseOrchestrator")
            .PersistKeysToDbContext<AppDbContext>();

        var section = config.GetSection(SectionName);
        var certificatePath = section["CertificatePath"];

        if (!string.IsNullOrWhiteSpace(certificatePath))
        {
            builder.ProtectKeysWithCertificate(LoadCertificate(certificatePath, section["CertificatePassword"]));
            return services;
        }

        if (section.GetValue("AllowUnprotectedKeys", false)) return services;

        // Development is exempt so a clone runs with no ceremony; its data is not worth stealing.
        var environment = config["ASPNETCORE_ENVIRONMENT"] ?? config["DOTNET_ENVIRONMENT"];
        if (string.Equals(environment, "Development", StringComparison.OrdinalIgnoreCase)) return services;

        throw new InvalidOperationException(
            $"{SectionName}:CertificatePath is not configured. The Data Protection key ring lives in the same "
            + "database as the access tokens it encrypts, so without a certificate to encrypt the keys "
            + "themselves, anyone holding a database dump holds the tokens too. Point "
            + $"{SectionName}__CertificatePath at a PKCS#12 file, or set {SectionName}__AllowUnprotectedKeys=true "
            + "to accept that risk deliberately.");
    }

    /// <summary>
    /// Loads the key-encryption certificate.
    /// </summary>
    /// <remarks>
    /// <see cref="X509CertificateLoader"/> rather than the <c>X509Certificate2</c> constructor: that
    /// constructor is obsolete from .NET 9, and on Windows it also wrote the key to a temporary file
    /// in the user store as a side effect of loading.
    /// </remarks>
    private static X509Certificate2 LoadCertificate(string path, string? password)
    {
        if (!File.Exists(path))
            throw new InvalidOperationException(
                $"{SectionName}:CertificatePath points at '{path}', which does not exist. In a container this is "
                + "usually a secret that was never mounted.");

        try
        {
            var certificate = X509CertificateLoader.LoadPkcs12FromFile(path, password);

            // A certificate without its private key can verify but not decrypt, so the key ring
            // would be write-only: every restart would mint new keys and every stored token would
            // become unreadable.
            if (!certificate.HasPrivateKey)
                throw new InvalidOperationException(
                    $"The certificate at '{path}' has no private key, so it cannot decrypt the key ring. "
                    + "Export it as PKCS#12 including the private key.");

            return certificate;
        }
        catch (Exception ex) when (ex is not InvalidOperationException)
        {
            throw new InvalidOperationException(
                $"The certificate at '{path}' could not be loaded. A wrong or missing "
                + $"{SectionName}:CertificatePassword is the usual cause.", ex);
        }
    }
}
