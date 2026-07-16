using System.Threading.RateLimiting;
using MassTransit;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using ReleaseOrchestrator.Ingress.Webhooks.Endpoints;
using ReleaseOrchestrator.Ingress.Webhooks.ExceptionHandling;
using ReleaseOrchestrator.Observability;
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
    builder.Services.AddSingleton(TimeProvider.System);
    builder.Services.AddHealthChecks();

    builder.Services.AddProblemDetails();
    // A downed broker must answer 503, not 500: senders treat 500 as permanent and drop the event.
    builder.Services.AddExceptionHandler<BrokerUnavailableExceptionHandler>();

    // No-op unless an OTLP endpoint is configured. Carries the trace across the queue into Core.
    builder.Services.AddReleaseOrchestratorTelemetry(
        builder.Configuration, "release-orchestrator-ingress", builder.Environment.EnvironmentName);

    builder.Services.Configure<ForwardedHeadersOptions>(o =>
    {
        o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        o.KnownIPNetworks.Clear();
        o.KnownProxies.Clear();
    });

    // These endpoints are internet-facing and guarded only by a shared secret. Without a
    // limit, that secret can be brute-forced as fast as the network allows.
    builder.Services.AddRateLimiter(o =>
    {
        o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
        o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
            RateLimitPartition.GetFixedWindowLimiter(
                ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions
                {
                    // README §9.1 budgets peaks of 5-10 events/sec across all connections.
                    PermitLimit = builder.Configuration.GetValue("RateLimit:WebhooksPerMinute", 1200),
                    Window = TimeSpan.FromMinutes(1)
                }));
    });

    builder.Services.AddMassTransit(x =>
    {
        x.UsingRabbitMq((_, cfg) =>
        {
            cfg.Host(builder.Configuration["Queue:Host"] ?? "localhost", h =>
            {
                h.Username(builder.Configuration["Queue:Username"]
                    ?? throw new InvalidOperationException("Queue:Username is not configured."));
                h.Password(builder.Configuration["Queue:Password"]
                    ?? throw new InvalidOperationException("Queue:Password is not configured."));
            });
        });
    });

    var app = builder.Build();

    app.UseForwardedHeaders();
    app.UseExceptionHandler();
    app.UseSerilogRequestLogging();

    if (app.Environment.IsDevelopment())
        app.MapOpenApi();
    else
        app.UseHsts();

    app.UseHttpsRedirection();
    app.UseRateLimiter();

    app.MapGitLabWebhooks();
    app.MapYandexTrackerWebhooks();

    // AllowAnonymous for symmetry with the core host: this endpoint must stay reachable if
    // authentication is ever added here.
    app.MapHealthChecks("/health").AllowAnonymous();

    app.Run();
    return 0;
}
catch (Exception ex)
{
    Log.Fatal(ex, "Ingress startup failed");
    // Non-zero exit so an orchestrator sees a failed container rather than a clean exit.
    return 1;
}
finally
{
    Log.CloseAndFlush();
}
