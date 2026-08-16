using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ReleaseOrchestrator.Infrastructure.Auth;
using Xunit;

namespace ReleaseOrchestrator.UnitTests;

/// <summary>
/// The key ring encrypts the VCS and tracker tokens and is stored in the same database as them,
/// so it must itself be encrypted with something that database does not hold. These tests pin the
/// rule that a deployment cannot reach that state by not noticing.
/// </summary>
public class DataProtectionSetupTests
{
    private static IConfiguration Configuration(params (string Key, string? Value)[] settings) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(settings.ToDictionary(s => s.Key, s => s.Value))
            .Build();

    private static void Register(IConfiguration config) =>
        new ServiceCollection().AddTokenProtection(config);

    [Fact]
    public void ProductionWithoutACertificateFailsFast()
    {
        var config = Configuration(("ASPNETCORE_ENVIRONMENT", "Production"));

        var exception = Assert.Throws<InvalidOperationException>(() => Register(config));

        // The message has to name both ways out, or it just says "no" to someone under pressure.
        Assert.Contains("CertificatePath", exception.Message, StringComparison.Ordinal);
        Assert.Contains("AllowUnprotectedKeys", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// No environment set at all is not Development - it is a deployment that forgot to say. The
    /// insecure default must not be the one you get by omission.
    /// </summary>
    [Fact]
    public void UnknownEnvironmentIsTreatedAsProduction()
        => Assert.Throws<InvalidOperationException>(() => Register(Configuration()));

    [Fact]
    public void DevelopmentRunsWithoutACertificate()
    {
        var exception = Record.Exception(() => Register(Configuration(("ASPNETCORE_ENVIRONMENT", "Development"))));

        Assert.Null(exception);
    }

    /// <summary>Accepting the risk is allowed - silently drifting into it is not.</summary>
    [Fact]
    public void ExplicitOptOutIsHonoured()
    {
        var config = Configuration(
            ("ASPNETCORE_ENVIRONMENT", "Production"),
            ("DataProtection:AllowUnprotectedKeys", "true"));

        Assert.Null(Record.Exception(() => Register(config)));
    }

    [Fact]
    public void AMissingCertificateFileIsReportedByPath()
    {
        var config = Configuration(
            ("ASPNETCORE_ENVIRONMENT", "Production"),
            ("DataProtection:CertificatePath", "/secrets/keyring-that-was-never-mounted.pfx"));

        var exception = Assert.Throws<InvalidOperationException>(() => Register(config));

        Assert.Contains("keyring-that-was-never-mounted.pfx", exception.Message, StringComparison.Ordinal);
    }
}
