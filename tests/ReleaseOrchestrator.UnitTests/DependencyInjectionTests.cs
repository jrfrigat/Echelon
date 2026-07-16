using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ReleaseOrchestrator.Application.Services;
using ReleaseOrchestrator.Infrastructure;
using ReleaseOrchestrator.Infrastructure.Queue.Consumers;
using Xunit;

namespace ReleaseOrchestrator.UnitTests;

/// <summary>
/// Smoke tests for the composition root. Every dependency here is registered lazily —
/// no database, broker or cache is contacted — so a constructor that asks for something
/// nobody registered is caught in seconds instead of at the first webhook in production.
/// </summary>
public class DependencyInjectionTests
{
    private static IConfiguration Configuration(Dictionary<string, string?>? overrides = null)
    {
        var settings = new Dictionary<string, string?>
        {
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

    [Fact]
    public void PlannerResolves()
    {
        using var provider = BuildProvider(Configuration());
        using var scope = provider.CreateScope();

        Assert.NotNull(scope.ServiceProvider.GetRequiredService<IReleasePlannerService>());
    }

    [Theory]
    [InlineData(typeof(MrOpenedConsumer))]
    [InlineData(typeof(MrStatusChangedConsumer))]
    [InlineData(typeof(TaskCreatedConsumer))]
    [InlineData(typeof(TaskStatusChangedConsumer))]
    [InlineData(typeof(ReleasePlanRecalculationConsumer))]
    public async Task ConsumerResolves(Type consumerType)
    {
        // Async disposal throughout: resolving a consumer pulls in MassTransit services that
        // implement only IAsyncDisposable, and a synchronous scope teardown throws on them.
        await using var provider = BuildProvider(Configuration());
        await using var scope = provider.CreateAsyncScope();

        Assert.NotNull(scope.ServiceProvider.GetRequiredService(consumerType));
    }

    /// <summary>
    /// External API clients must come from the typed-client registrations, or the timeout
    /// configured on them silently does not apply — they were bound to the concrete types
    /// while every consumer resolves the interface.
    /// </summary>
    [Theory]
    [InlineData(typeof(IVcsApiClient))]
    [InlineData(typeof(ITrackerApiClient))]
    public void ApiClientResolves(Type clientType)
    {
        using var provider = BuildProvider(Configuration());
        using var scope = provider.CreateScope();

        Assert.NotNull(scope.ServiceProvider.GetRequiredService(clientType));
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
