using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Rebus.OpenTelemetry.Configuration;

namespace ReleaseOrchestrator.Observability;

/// <summary>
/// Tracing and metrics for both hosts. Compiled into Web and linked into Ingress: the two hosts
/// must agree on service naming and on which sources are sampled, and the audit already paid for
/// duplicated rules drifting apart.
/// </summary>
/// <remarks>
/// Two ways out, wired independently because they answer to different consumers:
/// <list type="bullet">
///   <item>
///     Metrics are scraped by Prometheus over the <c>/metrics</c> endpoint this exposes (pull), and
///     also pushed over OTLP when a collector is configured. Prometheus is the default and needs no
///     collector — the process holds the numbers and Prometheus comes and gets them.
///   </item>
///   <item>
///     Traces go out over OTLP only. Prometheus is a metrics store with no notion of a span, so the
///     trace pipeline is registered exactly when an OTLP endpoint is set.
///   </item>
/// </list>
/// </remarks>
public static class TelemetryExtensions
{
    /// <summary>
    /// Registers metrics (for Prometheus and/or OTLP) and, when an OTLP endpoint is configured,
    /// tracing. Does nothing when Prometheus is disabled and no OTLP endpoint is set.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">Application configuration.</param>
    /// <param name="defaultServiceName">The service name to report when none is configured.</param>
    /// <param name="environmentName">The hosting environment, recorded as a resource attribute.</param>
    /// <returns>The same collection, for chaining.</returns>
    public static IServiceCollection AddReleaseOrchestratorTelemetry(
        this IServiceCollection services,
        IConfiguration configuration,
        string defaultServiceName,
        string environmentName)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var otlp = TelemetryOptions.Resolve(configuration, defaultServiceName);
        var prometheusEnabled = PrometheusEnabled(configuration);

        // Nobody is reading: no scrape endpoint, no push target. Register no meters and no tracer
        // so an unobserved host carries none of the cost.
        if (otlp is null && !prometheusEnabled)
            return services;

        var builder = services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(
                serviceName: ResolveServiceName(configuration, defaultServiceName),
                serviceVersion: ResolveServiceVersion(),
                // Distinguishes replicas: without it every pod's series collapse into one identity.
                serviceInstanceId: Environment.MachineName)
                .AddAttributes([new KeyValuePair<string, object>("deployment.environment", environmentName)]));

        // The instrumentation is the same whoever reads the numbers; only the exporters differ.
        builder.WithMetrics(metrics =>
        {
            metrics
                .AddAspNetCoreInstrumentation()   // request count, duration, active requests
                .AddHttpClientInstrumentation()   // outbound calls to GitLab and the tracker
                .AddRuntimeInstrumentation()      // GC, heap, thread pool, exception count
                .AddRebusInstrumentation();       // message send / receive / handle timings

            if (prometheusEnabled)
                metrics.AddPrometheusExporter();

            if (otlp is not null)
                metrics.AddOtlpExporter(exporter =>
                {
                    exporter.Endpoint = otlp.Endpoint;
                    exporter.Protocol = otlp.Protocol;
                });
        });

        if (otlp is not null)
        {
            builder.WithTracing(tracing => tracing
                .AddAspNetCoreInstrumentation(instrumentation =>
                {
                    // Health probes and metric scrapes run on a timer forever. Tracing them buries
                    // the real traffic and costs collector storage for no diagnostic value.
                    instrumentation.Filter = context => !IsInfrastructureRequest(context.Request.Path);
                    instrumentation.RecordException = true;
                })
                .AddHttpClientInstrumentation()
                // Rebus's own instrumentation. It emits a span per send and per handle and
                // propagates W3C trace context through the message headers, so the
                // Ingress → RabbitMQ → Core path stays one trace instead of three. Rebus core does
                // not do this; the Rebus.OpenTelemetry package is what registers the source.
                .AddRebusInstrumentation()
                .AddOtlpExporter(exporter =>
                {
                    exporter.Endpoint = otlp.Endpoint;
                    exporter.Protocol = otlp.Protocol;
                }));
        }

        return services;
    }

    /// <summary>
    /// Maps the Prometheus scrape endpoint (<c>/metrics</c>) when Prometheus is enabled.
    /// </summary>
    /// <remarks>
    /// A no-op when Prometheus is off, so both hosts call it unconditionally. The endpoint is
    /// anonymous and un-throttled, like the health probes: a scrape must reach the process even
    /// under a fallback authentication policy, and a monitoring pull must not spend the per-caller
    /// request budget meant for API traffic.
    /// </remarks>
    /// <param name="endpoints">The endpoint route builder.</param>
    /// <param name="configuration">Application configuration.</param>
    /// <returns>The same builder, for chaining.</returns>
    public static IEndpointRouteBuilder MapReleaseOrchestratorMetrics(
        this IEndpointRouteBuilder endpoints,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentNullException.ThrowIfNull(configuration);

        if (!PrometheusEnabled(configuration))
            return endpoints;

        endpoints.MapPrometheusScrapingEndpoint()
            .AllowAnonymous()
            .DisableRateLimiting();

        return endpoints;
    }

    private static bool PrometheusEnabled(IConfiguration configuration) =>
        configuration.GetValue("Prometheus:Enabled", true);

    private static string ResolveServiceName(IConfiguration configuration, string defaultServiceName)
    {
        var name = configuration["Otel:ServiceName"] ?? configuration["OTEL_SERVICE_NAME"];
        return string.IsNullOrWhiteSpace(name) ? defaultServiceName : name.Trim();
    }

    private static string ResolveServiceVersion() =>
        Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? Assembly.GetEntryAssembly()?.GetName().Version?.ToString()
        ?? "unknown";

    private static bool IsInfrastructureRequest(PathString path) =>
        path.StartsWithSegments("/health", StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments("/metrics", StringComparison.OrdinalIgnoreCase);
}
