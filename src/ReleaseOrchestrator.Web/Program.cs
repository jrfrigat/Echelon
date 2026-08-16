using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Identity.Web;
using ReleaseOrchestrator.Infrastructure;
using ReleaseOrchestrator.Infrastructure.Archive;
using ReleaseOrchestrator.Infrastructure.Auth;
using ReleaseOrchestrator.Infrastructure.Persistence;
using ReleaseOrchestrator.Observability;
using ReleaseOrchestrator.Web.Auth;
using ReleaseOrchestrator.Web.ExceptionHandling;
using ReleaseOrchestrator.Web.HealthChecks;
using Serilog;
using Serilog.Events;

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    builder.Host.UseSerilog((ctx, cfg) => cfg
        .ReadFrom.Configuration(ctx.Configuration)
        .Enrich.FromLogContext()
        .WriteTo.Console()
        .WriteTo.Seq(ctx.Configuration["Seq:Url"] ?? "http://localhost:5341"));

    builder.Services.AddOpenApi();

    builder.Services.AddControllers()
        // Enums must serialize as names. Without this they go out as integers while the PWA
        // models declare strings, so every screen reading an enum died on a JsonException.
        .AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));

    builder.Services.AddProblemDetails();
    builder.Services.AddExceptionHandler<DomainExceptionHandler>();
    builder.Services.AddInfrastructure(builder.Configuration);

    // Resources/ApiStrings.resx + .ru.resx, reached through IStringLocalizer<ApiStrings>.
    // No ResourcesPath on purpose: it would make the factory look for the resource under
    // <root>.Resources.<type name minus root> — i.e. ...Resources.Resources.ApiStrings — while the
    // resx actually compiles to the marker type's own full name. See ApiStrings for the details.
    builder.Services.AddLocalization();

    // Metrics for Prometheus to scrape at /metrics (on by default) and, when an OTLP endpoint is
    // configured, traces and metrics pushed to a collector as well. The /metrics endpoint itself is
    // mapped below.
    builder.Services.AddReleaseOrchestratorTelemetry(
        builder.Configuration, "release-orchestrator-core", builder.Environment.EnvironmentName);

    builder.Services.AddHealthChecks()
        .AddCheck<DatabaseHealthCheck>("database", tags: ["ready"])
        .AddCheck<ArchiveDatabaseHealthCheck>("archive-database", tags: ["ready"])
        .AddCheck<CoordinationHealthCheck>("coordination", tags: ["ready"]);

    // TLS terminates at a reverse proxy, so the original scheme arrives in a header.
    // Without this the app believes every request is plain HTTP.
    builder.Services.Configure<ForwardedHeadersOptions>(o =>
    {
        o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;

        // KNOWN GAP, deliberately left as-is rather than guessed at. Clearing both lists means
        // forwarded headers are honoured from ANY peer, so Connection.RemoteIpAddress downstream is
        // whatever the caller claimed. Two consequences worth knowing before relying on it:
        // the rate limiter's anonymous bucket can be reset per request by varying the header, and
        // the ForwardedIp recorded in the request audit is caller-supplied (which is why the audit
        // also stores the unforgeable transport peer separately, and labels the two differently).
        //
        // The fix is to name the actual proxy addresses or networks here, from configuration. That
        // cannot be guessed: set it wrong and forwarded headers stop being honoured at all, so every
        // client appears to come from the ingress and HTTPS detection breaks behind a TLS-terminating
        // proxy. It needs whoever knows the deployment topology.
        o.KnownIPNetworks.Clear();
        o.KnownProxies.Clear();
    });

    builder.Services.AddCors(o => o.AddDefaultPolicy(p => p
        .WithOrigins(builder.Configuration.GetSection("AllowedOrigins").Get<string[]>() ?? ["http://localhost:5173"])
        .AllowAnyHeader().AllowAnyMethod().AllowCredentials()));

    // Partitioned by caller. The user branch is the trustworthy one and is only reachable because
    // UseRateLimiter is registered after UseAuthentication -- see the comment there before moving
    // either. The address fallback covers unauthenticated traffic and is NOT trustworthy: forwarded
    // headers are currently accepted from any peer (see the ForwardedHeaders configuration above),
    // so an anonymous caller can mint a fresh bucket per request by varying X-Forwarded-For.
    // Closing that requires naming the real proxies in configuration, which is a deployment
    // decision rather than a code one.
    builder.Services.AddRateLimiter(o =>
    {
        o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
        o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
            RateLimitPartition.GetFixedWindowLimiter(
                ctx.User.Identity?.Name ?? ctx.Connection.RemoteIpAddress?.ToString() ?? "anonymous",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = builder.Configuration.GetValue("RateLimit:PermitsPerMinute", 300),
                    Window = TimeSpan.FromMinutes(1)
                }));
    });

    // The authentication scheme the deployment selected with Auth:Provider (AzureAd | Oidc | Local).
    builder.Services.AddConfiguredAuthentication(builder.Configuration);

    builder.Services.AddAuthorization(opts =>
    {
        opts.AddPolicy(Permissions.ReleasePlanApprove, p => p.AddRequirements(new PermissionRequirement(Permissions.ReleasePlanApprove)));
        opts.AddPolicy(Permissions.ConfigEdit, p => p.AddRequirements(new PermissionRequirement(Permissions.ConfigEdit)));
        opts.AddPolicy(Permissions.ReleasePlanView, p => p.AddRequirements(new PermissionRequirement(Permissions.ReleasePlanView)));
        opts.AddPolicy(Permissions.ReleaseExecute, p => p.AddRequirements(new PermissionRequirement(Permissions.ReleaseExecute)));
        opts.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
    });

    var app = builder.Build();

    // On by default: the app applies any pending migrations at startup, so a fresh or upgraded
    // deployment is ready without a separate step. Set Database:MigrateOnStartup=false for a
    // multi-replica deployment, where concurrent Migrate() calls would race and the app would need
    // standing DDL rights on the production database -- there, run migrations from an init container
    // or CI instead (docs/en/configuration.md, "Database").
    if (builder.Configuration.GetValue("Database:MigrateOnStartup", true))
    {
        using var scope = app.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.MigrateAsync();
        await scope.ServiceProvider.GetRequiredService<ArchiveDbContext>().Database.MigrateAsync();
        Log.Information("Applied EF Core migrations on startup.");
    }

    // FIRST, and above UseForwardedHeaders specifically. It captures the transport peer on the way
    // in -- before X-Forwarded-For rewrites it, and that header is accepted from anyone here -- and
    // reads the final status and the authenticated principal on the way out, because it sits
    // upstream of both the exception handler and authentication. Move it below either and it starts
    // recording confident nonsense: every handled failure as a 200 or 500, every caller as anonymous.
    // Nothing in the test suite would notice; this repo has no integration host.
    app.UseRequestAudit("core");

    app.UseForwardedHeaders();

    // Ahead of UseExceptionHandler so DomainExceptionHandler, which runs upstream of the
    // controllers, still resolves its title in the caller's language. Culture comes from the
    // Accept-Language header (the PWA sets it from the language picked in the UI, not the
    // browser's). Response bodies stay culture-invariant — System.Text.Json does not read
    // CurrentCulture for numbers or dates.
    string[] supportedCultures = ["en", "ru"];
    app.UseRequestLocalization(new RequestLocalizationOptions()
        .SetDefaultCulture("en")
        .AddSupportedCultures(supportedCultures)
        .AddSupportedUICultures(supportedCultures));

    // Request logging ABOVE the exception handler, not below it. Below, the logging middleware sees
    // the exception on its way up and records a 500 -- while the handler downstream converts it into
    // the 404 or 400 the caller actually received. Every domain failure was logged as a server fault.
    app.UseReleaseOrchestratorRequestLogging();
    app.UseExceptionHandler();

    if (app.Environment.IsDevelopment())
        app.MapOpenApi();
    else
        app.UseHsts();

    app.UseHttpsRedirection();

    app.Use(async (ctx, next) =>
    {
        var headers = ctx.Response.Headers;
        headers.XContentTypeOptions = "nosniff";
        headers.XFrameOptions = "DENY";
        headers["Referrer-Policy"] = "no-referrer";
        // The API serves the Blazor SPA from the same origin; wasm needs 'unsafe-eval'.
        headers.ContentSecurityPolicy =
            "default-src 'self'; script-src 'self' 'wasm-unsafe-eval'; style-src 'self' 'unsafe-inline'; "
            + "img-src 'self' data:; connect-src 'self'; frame-ancestors 'none'; base-uri 'self'";
        await next();
    });

    app.UseBlazorFrameworkFiles();
    app.UseStaticFiles();
    app.UseCors();
    app.UseAuthentication();

    // AFTER authentication, which is the whole point. The limiter partitions on the caller's name
    // and falls back to their address, but it used to run one line earlier -- before anything
    // populated ctx.User -- so the name was always null, the fallback was the only live branch, and
    // every authenticated caller was limited by an address that X-Forwarded-For lets them choose.
    // Per-user partitioning now actually happens, and a signed-in caller cannot escape their bucket
    // by rotating a header.
    //
    // The cost of the swap, stated plainly: token validation now runs before a request can be
    // rejected, so a caller holding one valid token can make the service do that work. It is the
    // standard ordering for user-partitioned limiting and the work is cached; being unable to limit
    // authenticated callers at all was the worse end of the trade.
    app.UseRateLimiter();
    app.UseAuthorization();
    app.MapControllers();

    // Liveness: the process is up. Readiness: it can actually serve.
    app.MapHealthChecks("/health", new HealthCheckOptions { Predicate = _ => false }).AllowAnonymous();
    app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = c => c.Tags.Contains("ready") }).AllowAnonymous();

    // /metrics for Prometheus (anonymous, un-throttled). No-op when Prometheus is disabled.
    app.MapReleaseOrchestratorMetrics(app.Configuration);

    // The Local provider issues its own tokens at /auth/login; map it only when Local is selected.
    if (string.Equals(app.Configuration["Auth:Provider"], "Local", StringComparison.OrdinalIgnoreCase))
        app.MapLocalAuthEndpoints();

    // The SPA shell must load anonymously — the fallback policy above requires an authenticated
    // user, which would 401 the Blazor host page and every deep link, so the admin UI never boots.
    // Auth happens inside the SPA (OIDC), and the data behind it stays protected by the API's own
    // policies; serving the static shell to anyone is the standard hosted-WASM setup.
    app.MapFallbackToFile("index.html").AllowAnonymous();

    app.Run();
    return 0;
}
// HostAbortedException is not a failure: it is how tooling stops this program once the host is
// built. `dotnet ef` throws it to grab the service provider without serving, and
// WebApplicationFactory throws it to take the host for a test. Catching it logged "Application
// startup failed" over a perfectly good run and, worse, hid the host from the factory -- every API
// test failed with "the entry point exited without ever building an IHost", which says nothing
// about the real cause.
catch (Exception ex) when (ex is not HostAbortedException)
{
    Log.Fatal(ex, "Application startup failed");
    // Non-zero exit: swallowing this returned 0, which Docker and Kubernetes read as a
    // clean shutdown, so a container that never served a request looked like a good deploy.
    return 1;
}
finally
{
    Log.CloseAndFlush();
}

/// <summary>
/// The host's entry point, named so tests can boot it.
/// </summary>
/// <remarks>
/// Top-level statements compile into an internal <c>Program</c> that
/// <c>WebApplicationFactory&lt;T&gt;</c> cannot reach. Declaring it public here is the documented way
/// to make the REAL host testable — which is the point: an API test against a hand-assembled
/// pipeline proves the test's wiring, not the deployment's.
/// </remarks>
public partial class Program;
