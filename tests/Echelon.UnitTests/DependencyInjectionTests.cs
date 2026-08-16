using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Rebus.Handlers;
using Echelon.Application.Contracts.Messages;
using Echelon.Application.Services;
using Echelon.Infrastructure;
using Echelon.Providers.Abstractions;
using Echelon.Providers.Abstractions.Tracker;
using Echelon.Providers.Abstractions.Vcs;
using Xunit;

namespace Echelon.UnitTests;

/// <summary>
/// Smoke tests for the composition root. Every dependency here is registered lazily -
/// no database, broker or cache is contacted - so a constructor that asks for something
/// nobody registered is caught in seconds instead of at the first webhook in production.
/// </summary>
public class DependencyInjectionTests
{
    private static IConfiguration Configuration(Dictionary<string, string?>? overrides = null)
    {
        var settings = new Dictionary<string, string?>
        {
            // Declared, not omitted: outside Development the composition root refuses to start
            // without a certificate to encrypt the Data Protection key ring, and an unset
            // environment counts as outside. See DataProtectionSetupTests for that rule itself.
            ["ASPNETCORE_ENVIRONMENT"] = "Development",
            ["ConnectionStrings:Default"] = "Server=unused;Database=unused;Trusted_Connection=True",
            ["ConnectionStrings:Archive"] = "Server=unused;Database=unused-archive;Trusted_Connection=True",
            ["Redis:ConnectionString"] = "localhost:6379",
            ["Queue:Host"] = "localhost",
            ["Queue:Username"] = "unused",
            ["Queue:Password"] = "unused"
        };

        if (overrides is not null)
            foreach (var (key, value) in overrides) settings[key] = value;

        return new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
    }

    private static ServiceProvider BuildProvider(IConfiguration config)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddInfrastructure(config);

        // ValidateOnBuild is the point: it resolves every registration's constructor rather
        // than waiting for the first request to hit an unsatisfiable dependency.
        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true
        });
    }

    [Fact]
    public void ContainerBuildsAndValidates()
    {
        using var provider = BuildProvider(Configuration());
        Assert.NotNull(provider);
    }

    /// <summary>
    /// The whole composition root, with no Redis configured at all.
    /// </summary>
    /// <remarks>
    /// Worth its own test rather than trusting the coordination unit tests: Redis was a required
    /// setting read at the top of AddInfrastructure, so "runs without Redis" is a claim about
    /// every registration downstream of it, not only about which cache is bound. ValidateOnBuild
    /// is what makes it a real check - it resolves every constructor rather than waiting for the
    /// first request to find one that still wants a multiplexer.
    /// </remarks>
    [Fact]
    public void ContainerBuildsWithNoRedisWhenTheDeploymentSaysItIsASingleInstance()
    {
        var config = Configuration(new Dictionary<string, string?>
        {
            ["Redis:ConnectionString"] = null,
            ["Coordination:Provider"] = "memory",
            ["Coordination:SingleInstance"] = "true"
        });

        using var provider = BuildProvider(config);

        Assert.NotNull(provider.GetRequiredService<IDistributedLease>());
    }

    [Fact]
    public void PlannerResolves()
    {
        using var provider = BuildProvider(Configuration());
        using var scope = provider.CreateScope();

        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IRolloutPlannerService>());
    }

    /// <summary>
    /// Every message has a handler registered. Rebus registers handlers by the message they handle,
    /// not by their concrete type, so this checks the <see cref="IHandleMessages{T}"/> registration.
    /// </summary>
    /// <remarks>
    /// It inspects the registration rather than resolving it, on purpose. Resolving the handler
    /// pulls in the bus, and resolving the bus opens a real RabbitMQ connection - which, with the
    /// throwaway credentials this test uses, hangs on a retry loop before failing. The registration
    /// is what proves the handler is wired; the connection is not this test's concern.
    /// </remarks>
    [Theory]
    [InlineData(typeof(MrOpened))]
    [InlineData(typeof(MrStatusChanged))]
    [InlineData(typeof(TaskCreated))]
    [InlineData(typeof(TaskStatusChanged))]
    [InlineData(typeof(TaskSyncRequested))]
    [InlineData(typeof(ReleasePlanRecalculationRequested))]
    public void EveryMessageHasARegisteredHandler(Type messageType)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddInfrastructure(Configuration());

        var handlerType = typeof(IHandleMessages<>).MakeGenericType(messageType);
        Assert.Contains(services, descriptor => descriptor.ServiceType == handlerType);
    }

    /// <summary>
    /// The provider factories are how anything reaches an external API now - the typed clients
    /// they replaced were resolved directly, and were bound to concrete types nobody asked for,
    /// which left their timeout silently unapplied.
    /// </summary>
    [Theory]
    [InlineData(typeof(IVcsProviderFactory))]
    [InlineData(typeof(ITrackerProviderFactory))]
    public void ProviderFactoryResolves(Type factoryType)
    {
        using var provider = BuildProvider(Configuration());
        using var scope = provider.CreateScope();

        Assert.NotNull(scope.ServiceProvider.GetRequiredService(factoryType));
    }

    /// <summary>
    /// The registry, asserted from the composition root.
    /// </summary>
    /// <remarks>
    /// Keyed services cannot be enumerated by key, so an adapter that is registered but not
    /// declared would resolve and still be invisible to the API's validation and to the
    /// factory's error message - a gap nothing else would catch, since both paths would simply
    /// report the provider as unknown.
    /// </remarks>
    [Theory]
    [InlineData("gitlab-webhook")]
    [InlineData("gitlab-poll")]
    public void VcsProviderIsRegisteredAndDiscoverable(string providerType)
    {
        using var provider = BuildProvider(Configuration());
        using var scope = provider.CreateScope();

        var factory = scope.ServiceProvider.GetRequiredService<IVcsProviderFactory>();

        Assert.Contains(providerType, factory.AvailableProviders);
        Assert.NotNull(scope.ServiceProvider.GetRequiredKeyedService<IVcsProviderAdapter>(providerType));
    }

    [Theory]
    [InlineData("yandextracker-webhook")]
    [InlineData("yandextracker-poll")]
    public void TrackerProviderIsRegisteredAndDiscoverable(string providerType)
    {
        using var provider = BuildProvider(Configuration());
        using var scope = provider.CreateScope();

        var factory = scope.ServiceProvider.GetRequiredService<ITrackerProviderFactory>();

        Assert.Contains(providerType, factory.AvailableProviders);
        Assert.NotNull(scope.ServiceProvider.GetRequiredKeyedService<ITrackerProviderAdapter>(providerType));
    }

    /// <summary>
    /// An unknown provider type must fail by naming the alternatives, and must do so before the
    /// connection's token is decrypted - the type is wrong, not the credentials.
    /// </summary>
    [Fact]
    public async Task UnknownVcsProviderFailsFastListingTheRegisteredOnes()
    {
        using var provider = BuildProvider(Configuration());
        using var scope = provider.CreateScope();

        var factory = scope.ServiceProvider.GetRequiredService<IVcsProviderFactory>();

        var connection = new VcsConnectionDescriptor(
            Name: "typo-connection",
            ProviderType: "gitab",
            ApiUrl: "https://gitlab.example.com",
            EncryptedAccessToken: []);

        var exception = await Assert.ThrowsAsync<UnknownProviderException>(
            () => factory.CreateAsync(connection, CancellationToken.None));

        Assert.Contains("gitab", exception.Message, StringComparison.Ordinal);
        Assert.Contains("gitlab", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The provider type is compared in canonical form, so what the UI sends ("GitLab") and what
    /// the database stores ("gitlab") are the same provider.
    /// </summary>
    [Theory]
    [InlineData("GitLab-Webhook")]
    [InlineData("  gitlab-poll  ")]
    public async Task VcsProviderTypeIsMatchedCaseInsensitively(string providerType)
    {
        using var provider = BuildProvider(Configuration());
        using var scope = provider.CreateScope();

        var factory = scope.ServiceProvider.GetRequiredService<IVcsProviderFactory>();

        var connection = new VcsConnectionDescriptor(
            Name: "gitlab-connection",
            ProviderType: providerType,
            ApiUrl: "https://gitlab.example.com",
            EncryptedAccessToken: []);

        // Only the lookup is under test. Binding goes on to fail on the empty token, which no
        // data-protection stack reachable from a unit test can decrypt - so the assertion is the
        // narrow one that can be made honestly: whatever went wrong, it was not "unknown
        // provider". Asserting "throws something" would pass on the very failure this rules out.
        var exception = await Record.ExceptionAsync(
            () => factory.CreateAsync(connection, CancellationToken.None));

        Assert.False(
            exception is UnknownProviderException,
            $"Provider type '{providerType}' should resolve to the registered gitlab adapter, "
            + $"but the factory reported it as unknown: {exception?.Message}");
    }

    [Theory]
    [InlineData("ConnectionStrings:Default")]
    [InlineData("ConnectionStrings:Archive")]
    [InlineData("Redis:ConnectionString")]
    [InlineData("Queue:Username")]
    [InlineData("Queue:Password")]
    public void MissingRequiredSettingFailsFast(string key)
    {
        // Fail at startup naming the setting, rather than silently defaulting: an empty SA
        // password used to leave the app in a restart loop with nothing pointing at the cause.
        var config = Configuration(new Dictionary<string, string?> { [key] = null });

        var exception = Assert.Throws<InvalidOperationException>(() => BuildProvider(config).Dispose());
        Assert.Contains(key.Split(':').Last(), exception.Message, StringComparison.OrdinalIgnoreCase);
    }
}
