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
        o.KnownIPNetworks.Clear();
        o.KnownProxies.Clear();
    });

    builder.Services.AddCors(o => o.AddDefaultPolicy(p => p
        .WithOrigins(builder.Configuration.GetSection("AllowedOrigins").Get<string[]>() ?? ["http://localhost:5173"])
        .AllowAnyHeader().AllowAnyMethod().AllowCredentials()));

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
        opts.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
    });

    var app = builder.Build();

    // Off by default: with more than one replica, concurrent Migrate() calls race, and the
    // app needs standing DDL rights on the production database. README §11.3 puts this in an
    // init container or CI. Turn on for single-instance or local runs.
    if (builder.Configuration.GetValue("Database:MigrateOnStartup", false))
    {
        using var scope = app.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<AppDbContext>().Database.MigrateAsync();
        await scope.ServiceProvider.GetRequiredService<ArchiveDbContext>().Database.MigrateAsync();
        Log.Information("Applied EF Core migrations on startup.");
    }

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

    app.UseExceptionHandler();
    app.UseSerilogRequestLogging();

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
    app.UseRateLimiter();
    app.UseAuthentication();
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
catch (Exception ex)
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
