using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using ReleaseOrchestrator.Application.Services;
using ReleaseOrchestrator.Infrastructure.Coordination;
using ReleaseOrchestrator.Providers.Abstractions;
using Xunit;

namespace ReleaseOrchestrator.UnitTests.Coordination;

/// <summary>
/// Choosing what carries the permission cache and the job lease.
/// </summary>
/// <remarks>
/// Both are selectable because neither is a source of truth: the permission stamp is a hash of the
/// stored rules, so losing the cache costs reads and not correctness, and the lease guards
/// idempotent work against being done N times rather than guarding data. What is not free is
/// choosing "memory" for a deployment that is not one process - hence the tests about refusing to
/// get there quietly.
/// </remarks>
public class CoordinationSetupTests
{
    private static IConfiguration Configuration(params (string Key, string? Value)[] settings) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(settings.ToDictionary(s => s.Key, s => s.Value))
            .Build();

    private static ServiceProvider Build(IConfiguration config)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(TimeProvider.System);
        services.AddCoordination(config);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void MemoryProviderKeepsTheLeaseInThisProcess()
    {
        using var provider = Build(Configuration(
            ("Coordination:Provider", "memory"),
            ("Coordination:SingleInstance", "true")));

        Assert.IsType<InProcessLease>(provider.GetRequiredService<IDistributedLease>());
        Assert.NotNull(provider.GetRequiredService<IDistributedCache>());
    }

    /// <summary>
    /// The whole point of naming the provider: a deployment can drop Redis, but not by forgetting
    /// to configure it.
    /// </summary>
    [Fact]
    public void MemoryProviderRefusesToStartWithoutTheSingleInstanceAssertion()
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => Build(Configuration(("Coordination:Provider", "memory"))));

        Assert.Contains("SingleInstance", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Redis stays the default, so an existing deployment that says nothing keeps the behaviour it
    /// has - and a missing connection string still fails at startup rather than at the first lease.
    /// </summary>
    [Fact]
    public void TheDefaultIsRedisAndItStillDemandsItsConnectionString()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => Build(Configuration()));

        Assert.Contains("Redis:ConnectionString", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// An unconfigured Redis must not silently become a single-instance deployment: that is the
    /// typo that turns three replicas into three archive cycles.
    /// </summary>
    [Fact]
    public void AMissingRedisConnectionStringDoesNotQuietlyBecomeSingleInstance()
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => Build(Configuration(("Coordination:SingleInstance", "true"))));

        Assert.Contains("Redis:ConnectionString", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnknownProviderNamesTheOnesThatExist()
    {
        var exception = Assert.Throws<UnknownProviderException>(
            () => Build(Configuration(("Coordination:Provider", "memcached"))));

        Assert.Contains("memcached", exception.Message, StringComparison.Ordinal);
        Assert.Contains("redis", exception.Message, StringComparison.Ordinal);
        Assert.Contains("memory", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The value is typed into compose by a person, so it is matched the way every other provider
    /// key here is.
    /// </summary>
    [Theory]
    [InlineData("Memory")]
    [InlineData("  memory  ")]
    public void TheProviderNameIsMatchedInCanonicalForm(string provider)
    {
        using var built = Build(Configuration(
            ("Coordination:Provider", provider),
            ("Coordination:SingleInstance", "true")));

        Assert.IsType<InProcessLease>(built.GetRequiredService<IDistributedLease>());
    }
}

/// <summary>
/// The in-process lease. Not a stub: with one replica, "once across every replica" and "once in
/// this process" are the same sentence, and the part that has to work is refusing the second
/// caller.
/// </summary>
public class InProcessLeaseTests
{
    private static readonly DateTime Now = new(2026, 7, 17, 12, 0, 0, DateTimeKind.Utc);
    private static CancellationToken Ct => CancellationToken.None;

    private static InProcessLease Lease(TimeProvider clock) =>
        new(clock, NullLogger<InProcessLease>.Instance);

    [Fact]
    public async Task AFreeLeaseIsGranted()
    {
        await using var held = await Lease(new FakeTimeProvider(Now)).TryAcquireAsync("job", TimeSpan.FromMinutes(5), Ct);

        Assert.NotNull(held);
    }

    [Fact]
    public async Task AHeldLeaseIsRefused()
    {
        var lease = Lease(new FakeTimeProvider(Now));
        await using var first = await lease.TryAcquireAsync("job", TimeSpan.FromMinutes(5), Ct);

        var second = await lease.TryAcquireAsync("job", TimeSpan.FromMinutes(5), Ct);

        Assert.NotNull(first);
        Assert.Null(second);
    }

    [Fact]
    public async Task ReleasingLetsTheNextCallerIn()
    {
        var lease = Lease(new FakeTimeProvider(Now));
        var first = await lease.TryAcquireAsync("job", TimeSpan.FromMinutes(5), Ct);
        Assert.NotNull(first);
        await first.DisposeAsync();

        await using var second = await lease.TryAcquireAsync("job", TimeSpan.FromMinutes(5), Ct);

        Assert.NotNull(second);
    }

    /// <summary>
    /// Different jobs do not exclude each other: the archive cycle and the reconciliation sweep
    /// hold separate leases and are meant to run at the same time.
    /// </summary>
    [Fact]
    public async Task DifferentJobsDoNotBlockEachOther()
    {
        var lease = Lease(new FakeTimeProvider(Now));

        await using var archive = await lease.TryAcquireAsync("archive-cycle", TimeSpan.FromMinutes(5), Ct);
        await using var reconciliation = await lease.TryAcquireAsync("task-reconciliation", TimeSpan.FromMinutes(5), Ct);

        Assert.NotNull(archive);
        Assert.NotNull(reconciliation);
    }

    /// <summary>
    /// Expiry is honoured rather than assumed away. A replica that is killed cannot release
    /// anything, and a single-instance deployment must not be the one place where a cycle that
    /// overran blocks every later pass forever.
    /// </summary>
    [Fact]
    public async Task AnExpiredLeaseCanBeTakenAgain()
    {
        var clock = new FakeTimeProvider(Now);
        var lease = Lease(clock);

        var first = await lease.TryAcquireAsync("job", TimeSpan.FromMinutes(5), Ct);
        Assert.NotNull(first);

        // Never disposed: this is the killed-replica case, not the tidy one.
        clock.Advance(TimeSpan.FromMinutes(6));

        await using var second = await lease.TryAcquireAsync("job", TimeSpan.FromMinutes(5), Ct);

        Assert.NotNull(second);
    }

    [Fact]
    public async Task ALeaseThatHasNotExpiredIsStillRefused()
    {
        var clock = new FakeTimeProvider(Now);
        var lease = Lease(clock);

        await using var first = await lease.TryAcquireAsync("job", TimeSpan.FromMinutes(5), Ct);
        clock.Advance(TimeSpan.FromMinutes(4));

        Assert.Null(await lease.TryAcquireAsync("job", TimeSpan.FromMinutes(5), Ct));
    }
}
