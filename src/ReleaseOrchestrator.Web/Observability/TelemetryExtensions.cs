using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace ReleaseOrchestrator.Observability;

/// <summary>
/// OTLP tracing and metrics for both hosts. Compiled into Web and linked into Ingress: the two
/// hosts must agree on service naming and on which sources are sampled, and the audit already
/// paid for duplicated rules drifting apart.
/// </summary>
public static class TelemetryExtensions
{
    /// <summary>
    /// MassTransit's own <see cref="System.Diagnostics.ActivitySource"/> name
    /// (<c>MassTransit.Logging.DiagnosticHeaders.DefaultListenerName</c>). Without this source the
    /// Ingress → RabbitMQ → Core path breaks into disconnected traces, which is the single thing
    /// this telemetry exists to fix. Spelled as a literal so the shared file stays free of a
    /// MassTransit reference, which Web only has transitively.
    /// </summary>
    private const string MassTransitSource = "MassTransit";

    /// <summary>MassTransit's meter name (<c>MassTransit.Monitoring.InstrumentationOptions.MeterName</c>).</summary>
    private const string MassTransitMeter = "MassTransit";

    /// <summary>
    /// Registers OTLP export when an endpoint is configured, otherwise does nothing at all.
    /// </summary>
    public static IServiceCollection AddReleaseOrchestratorTelemetry(
        this IServiceCollection services,
        IConfiguration configuration,
        string defaultServiceName,
        string environmentName)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = TelemetryOptions.Resolve(configuration, defaultServiceName);
        if (options is null)
            return services;

        services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(
                serviceName: options.ServiceName,
                serviceVersion: ResolveServiceVersion(),
                // Distinguishes replicas: without it every pod's spans collapse into one identity.
                serviceInstanceId: Environment.MachineName)
                .AddAttributes([new KeyValuePair<string, object>("deployment.environment", environmentName)]))
            .WithTracing(tracing => tracing
                .AddAspNetCoreInstrumentation(instrumentation =>
                {
                    // Health probes and metrics scrapes run on a timer forever. Tracing them buries
                    // the real traffic and costs collector storage for no diagnostic value.
                    instrumentation.Filter = context => !IsInfrastructureRequest(context.Request.Path);
                    instrumentation.RecordException = true;
                })
                .AddSource(MassTransitSource)
                .AddOtlpExporter(exporter =>
                {
                    exporter.Endpoint = options.Endpoint;
                    exporter.Protocol = options.Protocol;
                }))
            .WithMetrics(metrics => metrics
                .AddAspNetCoreInstrumentation()
                .AddMeter(MassTransitMeter)
                .AddOtlpExporter(exporter =>
                {
                    exporter.Endpoint = options.Endpoint;
                    exporter.Protocol = options.Protocol;
                }));

        return services;
    }

    private static string ResolveServiceVersion() =>
        Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? Assembly.GetEntryAssembly()?.GetName().Version?.ToString()
        ?? "unknown";

    private static bool IsInfrastructureRequest(PathString path) =>
        path.StartsWithSegments("/health", StringComparison.OrdinalIgnoreCase);
}
