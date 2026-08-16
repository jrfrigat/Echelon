using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Rebus.Bus;
using Rebus.Bus.Advanced;
using ReleaseOrchestrator.Application.Contracts.Messages;
using ReleaseOrchestrator.Infrastructure.Archive;
using ReleaseOrchestrator.Infrastructure.Auth;
using ReleaseOrchestrator.Infrastructure.Persistence;

namespace ReleaseOrchestrator.UnitTests.Api;

/// <summary>
/// Boots the real web host in-process, over SQLite, with authentication and the bus stubbed.
/// </summary>
/// <remarks>
/// <para>
/// The point is to exercise what the deployment actually runs: routing, model binding, the
/// authorization policies, the JSON options, the exception handler, and the controllers on top of
/// real services and a real database. Everything below the API had tests; the API itself had none,
/// and its shape is what a browser and every integration depends on.
/// </para>
/// <para>
/// Four things are replaced, each for a reason that would otherwise make the test prove something
/// else:
/// </para>
/// <list type="bullet">
/// <item>the DATABASE, with SQLite — same EF model and the same queries, without needing a server;</item>
/// <item>AUTHENTICATION, with a scheme that issues whatever permissions a test asks for. The real one
/// is JWT against an identity provider; making tests obtain a token would test that provider, and
/// entering a password to get one is not something this suite should ever need;</item>
/// <item>the BUS, with a recorder. The endpoints publish recalculation requests, and a test asserting
/// that they did should not require a broker;</item>
/// <item>the HOSTED SERVICES — archive, polling, rollout coordination — because a test that starts
/// background workers is a test with a clock in it.</item>
/// </list>
/// <para>
/// Nothing else is stubbed. In particular the planner, the ordering-rule reader and writer, and the
/// EF model are the production ones.
/// </para>
/// </remarks>
public sealed class ApiFactory : WebApplicationFactory<Program>
{
    private SqliteConnection _operational = null!;
    private SqliteConnection _archive = null!;

    /// <summary>Messages the endpoints asked the bus to send, in order.</summary>
    public RecordingBus Bus { get; } = new();

    /// <summary>Permissions the next requests will carry. Empty means an authenticated user with none.</summary>
    public List<string> Permissions { get; } = [.. new[]
    {
        Infrastructure.Auth.Permissions.ReleasePlanView,
        Infrastructure.Auth.Permissions.ReleasePlanApprove,
        Infrastructure.Auth.Permissions.ReleaseExecute,
        Infrastructure.Auth.Permissions.ConfigEdit
    }];

    /// <inheritdoc/>
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        // Enough configuration for the host's own startup validation to pass. It validates eagerly and
        // by design, so these are not optional -- see AddCoordination and AddMessaging.
        builder.UseSetting("Database:Provider", "sqlserver");
        builder.UseSetting("Database:MigrateOnStartup", "false");
        builder.UseSetting("ConnectionStrings:Default", "Server=unused;Database=unused;Integrated Security=true");
        builder.UseSetting("ConnectionStrings:Archive", "Server=unused;Database=unused;Integrated Security=true");
        builder.UseSetting("Coordination:Provider", "memory");
        builder.UseSetting("Coordination:SingleInstance", "true");
        builder.UseSetting("Queue:Host", "unused");
        builder.UseSetting("Queue:Username", "unused");
        builder.UseSetting("Queue:Password", "unused");
        builder.UseSetting("Auth:Provider", "Oidc");
        builder.UseSetting("Auth:Oidc:Authority", "https://authority.invalid");

        // The host refuses to start without a certificate to encrypt the Data Protection key ring,
        // because that ring lives in the same database as the tokens it protects. Accepted here
        // deliberately: this database is in memory, holds no token, and dies with the test.
        builder.UseSetting("DataProtection:AllowUnprotectedKeys", "true");

        builder.ConfigureServices(services =>
        {
            RemoveHostedServices(services);
            ReplaceDatabases(services);
            ReplaceBus(services);
            ReplaceAuthentication(services);
        });
    }

    /// <summary>
    /// Drops the background workers. They are the reason a test host would otherwise poll GitLab,
    /// run an archive cycle and drive rollouts while a test asserts on a controller.
    /// </summary>
    private static void RemoveHostedServices(IServiceCollection services)
    {
        foreach (var descriptor in services.Where(d => d.ServiceType == typeof(IHostedService)).ToList())
            services.Remove(descriptor);
    }

    /// <summary>
    /// Swaps both contexts onto SQLite, keeping one open connection each so the in-memory database
    /// outlives the pooled connections EF opens and closes at will.
    /// </summary>
    private void ReplaceDatabases(IServiceCollection services)
    {
        _operational = new SqliteConnection("DataSource=:memory:");
        _archive = new SqliteConnection("DataSource=:memory:");
        _operational.Open();
        _archive.Open();

        Replace<AppDbContext>(services, _operational);
        Replace<ArchiveDbContext>(services, _archive);

        static void Replace<TContext>(IServiceCollection services, SqliteConnection connection)
            where TContext : DbContext
        {
            // Everything the host's AddDbContext put in, not just the context and its options. EF 9
            // registers the provider through IDbContextOptionsConfiguration<TContext>, and leaving
            // that behind means SQL Server and SQLite are both configured on one context -- which EF
            // rejects at resolve time with "only a single database provider can be registered".
            foreach (var descriptor in services
                .Where(d => d.ServiceType == typeof(TContext)
                            || d.ServiceType == typeof(DbContextOptions)
                            || d.ServiceType == typeof(DbContextOptions<TContext>)
                            || (d.ServiceType.IsGenericType
                                && d.ServiceType.GetGenericArguments().Contains(typeof(TContext))))
                .ToList())
                services.Remove(descriptor);

            services.AddDbContext<TContext>(o => o.UseSqlite(connection));
        }
    }

    private void ReplaceBus(IServiceCollection services)
    {
        foreach (var descriptor in services.Where(d => d.ServiceType == typeof(IBus)).ToList())
            services.Remove(descriptor);

        services.AddSingleton<IBus>(Bus);
    }

    /// <summary>
    /// Replaces JWT bearer with a scheme that authenticates every request as a test operator.
    /// </summary>
    /// <remarks>
    /// The permission CLAIMS are issued here, but the authorization POLICIES are the host's own — so a
    /// test asking for a plan without <c>ReleasePlanApprove</c> is refused by the same handler
    /// production uses. That is the half worth testing; obtaining a real token is the identity
    /// provider's job, not this suite's.
    /// </remarks>
    private void ReplaceAuthentication(IServiceCollection services)
    {
        services.AddSingleton(this);
        services.AddAuthentication(TestAuthHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, TestAuthHandler>(TestAuthHandler.SchemeName, _ => { });

        // The host registers an IClaimsTransformation that resolves permissions from the database.
        // It would overwrite the claims this scheme issues with whatever the (empty) grants table
        // says, so the test's own permissions have to be the last word.
        //
        // REPLACED, not removed: the authentication service takes IClaimsTransformation as a required
        // constructor argument, so removing it fails service validation at startup rather than
        // leaving the claims alone.
        services.RemoveAll<IClaimsTransformation>();
        services.AddSingleton<IClaimsTransformation, NoopClaimsTransformation>();
    }

    /// <summary>Creates the schema. Call once per test, after the host is up.</summary>
    public async Task InitializeDatabaseAsync()
    {
        using var scope = Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.EnsureCreatedAsync();
        await scope.ServiceProvider.GetRequiredService<ArchiveDbContext>().Database.EnsureCreatedAsync();
    }

    /// <summary>Runs work against the operational database, in the host's own service scope.</summary>
    public async Task<T> WithDbAsync<T>(Func<AppDbContext, Task<T>> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        using var scope = Services.CreateScope();
        return await work(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing) return;

        _operational?.Dispose();
        _archive?.Dispose();
    }
}

/// <summary>Authenticates every request as a test operator holding <see cref="ApiFactory.Permissions"/>.</summary>
public sealed class TestAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    ApiFactory factory) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    /// <summary>The scheme name; also the default, so <c>[Authorize]</c> with no scheme picks it.</summary>
    public const string SchemeName = "Test";

    /// <inheritdoc/>
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        // Honours an explicit "anonymous" marker so a test can prove an endpoint refuses an
        // unauthenticated caller -- without it, this scheme would make every 401 unreachable.
        if (Request.Headers.ContainsKey("X-Test-Anonymous"))
            return Task.FromResult(AuthenticateResult.NoResult());

        List<Claim> claims =
        [
            new(ClaimTypes.NameIdentifier, "00000000-0000-0000-0000-0000000000ff"),
            new("oid", "00000000-0000-0000-0000-0000000000ff"),
            new("name", "Test Operator")
        ];

        claims.AddRange(factory.Permissions.Select(p => new Claim(PermissionClaimsTransformation.PermissionClaimType, p)));

        var identity = new ClaimsIdentity(claims, SchemeName, "name", ClaimTypes.Role);
        return Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
    }
}

/// <summary>An <see cref="IBus"/> that records instead of sending.</summary>
public sealed class RecordingBus : IBus
{
    /// <summary>Everything the application asked to send, in order.</summary>
    public List<object> Sent { get; } = [];

    /// <inheritdoc/>
    public Task Send(object commandMessage, IDictionary<string, string>? optionalHeaders = null)
    {
        Sent.Add(commandMessage);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task Publish(object eventMessage, IDictionary<string, string>? optionalHeaders = null)
    {
        Sent.Add(eventMessage);
        return Task.CompletedTask;
    }

    /// <summary>The recalculation requests the application asked for.</summary>
    public IEnumerable<ReleasePlanRecalculationRequested> Recalculations =>
        Sent.OfType<ReleasePlanRecalculationRequested>();

    /// <inheritdoc/>
    public Task SendLocal(object commandMessage, IDictionary<string, string>? optionalHeaders = null) =>
        Send(commandMessage, optionalHeaders);

    /// <inheritdoc/>
    public Task Reply(object replyMessage, IDictionary<string, string>? optionalHeaders = null) =>
        Send(replyMessage, optionalHeaders);

    /// <inheritdoc/>
    public Task Defer(TimeSpan delay, object message, IDictionary<string, string>? optionalHeaders = null) =>
        Send(message, optionalHeaders);

    /// <inheritdoc/>
    public Task DeferLocal(TimeSpan delay, object message, IDictionary<string, string>? optionalHeaders = null) =>
        Send(message, optionalHeaders);

    /// <inheritdoc/>
    public Task Subscribe<TEvent>() => Task.CompletedTask;

    /// <inheritdoc/>
    public Task Subscribe(Type eventType) => Task.CompletedTask;

    /// <inheritdoc/>
    public Task Unsubscribe<TEvent>() => Task.CompletedTask;

    /// <inheritdoc/>
    public Task Unsubscribe(Type eventType) => Task.CompletedTask;

    /// <inheritdoc/>
    public IAdvancedApi Advanced => throw new NotSupportedException("The recording bus has no advanced API.");

    /// <inheritdoc/>
    public void Dispose() { }
}

/// <summary>Leaves the principal exactly as the authentication scheme issued it.</summary>
/// <remarks>
/// Stands in for the host's database-backed permission transformation. That one is unit-tested on
/// its own; here it would only replace the test's permissions with the empty grants table.
/// </remarks>
public sealed class NoopClaimsTransformation : IClaimsTransformation
{
    /// <inheritdoc/>
    public Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal) => Task.FromResult(principal);
}
