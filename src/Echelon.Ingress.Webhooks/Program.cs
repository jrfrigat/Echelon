using System.Threading.RateLimiting;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Rebus.Config;
using Rebus.Routing.TypeBased;
using Rebus.ServiceProvider;
using Echelon.Application.Contracts.Messages;
using Echelon.Ingress.Webhooks;
using Echelon.Ingress.Webhooks.Endpoints;
using Echelon.Ingress.Webhooks.ExceptionHandling;
using Echelon.Observability;
using Echelon.Providers.GitLab;
using Echelon.Providers.YandexTracker;
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

    // Metrics for Prometheus to scrape at /metrics (on by default); when an OTLP endpoint is set it
    // also pushes to a collector and carries the trace across the queue into Core. /metrics is
    // mapped below.
    builder.Services.AddEchelonTelemetry(
        builder.Configuration, "echelon-ingress", builder.Environment.EnvironmentName);

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
                    // 1200/min is ~20/sec per source address, against a budgeted peak of 5-10
                    // events/sec across all connections -- headroom for a burst without leaving the
                    // signature check unthrottled.
                    PermitLimit = builder.Configuration.GetValue("RateLimit:WebhooksPerMinute", 1200),
                    Window = TimeSpan.FromMinutes(1)
                }));
    });

    // A send-only bus: the ingress validates a webhook and forwards it to Core, and reads nothing
    // back, so it has no input queue of its own. Every message type is routed to Core's queue, so
    // an endpoint calls bus.Send and never names a destination.
    builder.Services.AddRebus(configure => configure
        .Transport(t => t.UseRabbitMqAsOneWayClient(IngressMessaging.RabbitMqConnectionString(builder.Configuration)))
        .Routing(r => r.TypeBased().MapAssemblyDerivedFrom<IMessage>(MessageRouting.InputQueue)));

    // The webhook parsers, keyed by provider type. This is the ingress's only provider registration:
    // it wants each provider's webhook knowledge but not its HTTP read-adapter (it never calls a
    // provider's API), so it adds the parsers alone rather than the whole provider.
    builder.Services.AddGitLabWebhookParser();
    builder.Services.AddYandexTrackerWebhookParser();

    var app = builder.Build();

    app.UseForwardedHeaders();

    // Above the exception handler, for the reason spelled out in the core host: below it, a handled
    // failure is logged as the 500 it briefly was rather than the status the caller was sent.
    app.UseEchelonRequestLogging();
    app.UseExceptionHandler();

    if (app.Environment.IsDevelopment())
        app.MapOpenApi();
    else
        app.UseHsts();

    app.UseHttpsRedirection();
    app.UseRateLimiter();

    // One route per registered provider, built from the providers' own descriptors. No provider is
    // named here - adding one is a parser registration above and nothing on this line.
    app.MapProviderWebhooks();

    // AllowAnonymous for symmetry with the core host: this endpoint must stay reachable if
    // authentication is ever added here.
    app.MapHealthChecks("/health").AllowAnonymous();

    // /metrics for Prometheus (anonymous, un-throttled). No-op when Prometheus is disabled.
    app.MapEchelonMetrics(app.Configuration);

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
