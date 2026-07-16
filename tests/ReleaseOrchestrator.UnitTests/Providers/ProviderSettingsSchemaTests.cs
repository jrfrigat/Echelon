using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ReleaseOrchestrator.Infrastructure;
using ReleaseOrchestrator.Providers.Abstractions;
using ReleaseOrchestrator.Providers.Abstractions.Tracker;
using ReleaseOrchestrator.Providers.Abstractions.Vcs;
using ReleaseOrchestrator.Providers.YandexTracker;
using Xunit;

namespace ReleaseOrchestrator.UnitTests.Providers;

/// <summary>
/// The settings schema is what lets the admin UI configure a provider it has never heard of. If it
/// drifts from what the adapter actually reads, the form asks for the wrong thing and the
/// connection fails at first use — so the two are asserted against each other here.
/// </summary>
public class ProviderSettingsSchemaTests
{
    private static ServiceProvider BuildProvider()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Default"] = "Server=unused;Database=unused;Trusted_Connection=True",
            ["ConnectionStrings:Archive"] = "Server=unused;Database=unused-archive;Trusted_Connection=True",
            ["Redis:ConnectionString"] = "localhost:6379",
            ["Queue:Host"] = "localhost",
            ["Queue:Username"] = "unused",
            ["Queue:Password"] = "unused"
        }).Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddInfrastructure(config);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void YandexTrackerDeclaresTheOrganizationIdItActuallyReads()
    {
        using var provider = BuildProvider();
        using var scope = provider.CreateScope();

        var schema = scope.ServiceProvider
            .GetRequiredService<ITrackerProviderFactory>()
            .GetSettingsSchema("yandextracker");

        var orgId = Assert.Single(schema);

        // The key is the contract between the form and YandexTrackerOptions.From. A rename on one
        // side alone leaves the adapter throwing "requires an organization id" at a connection the
        // operator plainly filled in.
        Assert.Equal(YandexTrackerOptions.OrgIdKey, orgId.Key);
        Assert.True(orgId.Required);
        Assert.False(string.IsNullOrWhiteSpace(orgId.Label));
    }

    /// <summary>
    /// GitLab needs nothing beyond a URL and a token, and that has to be expressible. A contract
    /// where every provider must have settings is how Yandex's organization id ended up in the
    /// shared entity in the first place.
    /// </summary>
    [Fact]
    public void GitLabDeclaresNoSettings()
    {
        using var provider = BuildProvider();
        using var scope = provider.CreateScope();

        var schema = scope.ServiceProvider
            .GetRequiredService<IVcsProviderFactory>()
            .GetSettingsSchema("gitlab");

        Assert.Empty(schema);
    }

    [Theory]
    [InlineData("YandexTracker")]
    [InlineData("  yandextracker  ")]
    public void SchemaLookupMatchesTheProviderTypeCanonically(string providerType)
    {
        using var provider = BuildProvider();
        using var scope = provider.CreateScope();

        var schema = scope.ServiceProvider
            .GetRequiredService<ITrackerProviderFactory>()
            .GetSettingsSchema(providerType);

        Assert.NotEmpty(schema);
    }

    /// <summary>
    /// An unknown provider names the alternatives here too. The UI asks for a schema before it can
    /// render a form, so this is the first place a typo surfaces.
    /// </summary>
    [Fact]
    public void UnknownProviderFailsListingTheRegisteredOnes()
    {
        using var provider = BuildProvider();
        using var scope = provider.CreateScope();

        var factory = scope.ServiceProvider.GetRequiredService<IVcsProviderFactory>();

        var exception = Assert.Throws<UnknownProviderException>(() => factory.GetSettingsSchema("gitab"));

        Assert.Contains("gitab", exception.Message, StringComparison.Ordinal);
        Assert.Contains("gitlab", exception.Message, StringComparison.Ordinal);
    }
}
