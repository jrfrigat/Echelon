using Microsoft.Extensions.Configuration;
using OpenTelemetry.Exporter;

namespace Echelon.Observability;

/// <summary>
/// Resolved OTLP telemetry settings for a single host.
/// </summary>
public sealed record TelemetryOptions
{
    private TelemetryOptions(string serviceName, Uri endpoint, OtlpExportProtocol protocol)
    {
        ServiceName = serviceName;
        Endpoint = endpoint;
        Protocol = protocol;
    }

    /// <summary>The name this host reports itself under. Distinguishes the API from the webhook host.</summary>
    public string ServiceName { get; }

    /// <summary>Where the collector listens.</summary>
    public Uri Endpoint { get; }

    /// <summary>Which OTLP transport to use. gRPC unless the configuration names <c>http/protobuf</c>.</summary>
    public OtlpExportProtocol Protocol { get; }

    /// <summary>
    /// Reads <c>Otel__*</c> settings, falling back to the standard <c>OTEL_EXPORTER_OTLP_*</c>
    /// environment variables that every OpenTelemetry SDK understands.
    /// </summary>
    /// <returns>
    /// <c>null</c> when telemetry is off or unusable. Telemetry is opt-in on purpose: without a
    /// collector there is nowhere to export to, and observability must never be the reason a
    /// release-orchestration host fails to start. A malformed endpoint is treated the same way -
    /// it disables export rather than throwing, because the value arrives from the environment
    /// and a typo in a compose file should not take the service down.
    /// </returns>
    public static TelemetryOptions? Resolve(IConfiguration configuration, string defaultServiceName)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var rawEndpoint = configuration["Otel:Endpoint"]
            ?? configuration["OTEL_EXPORTER_OTLP_ENDPOINT"];

        if (string.IsNullOrWhiteSpace(rawEndpoint))
            return null;

        // Explicit Otel__Enabled=false wins over a configured endpoint, so telemetry can be cut
        // without unpicking the endpoint from the environment.
        if (!configuration.GetValue("Otel:Enabled", true))
            return null;

        if (!Uri.TryCreate(rawEndpoint.Trim(), UriKind.Absolute, out var endpoint))
            return null;

        var serviceName = configuration["Otel:ServiceName"]
            ?? configuration["OTEL_SERVICE_NAME"];

        if (string.IsNullOrWhiteSpace(serviceName))
            serviceName = defaultServiceName;

        return new TelemetryOptions(serviceName.Trim(), endpoint, ResolveProtocol(configuration));
    }

    private static OtlpExportProtocol ResolveProtocol(IConfiguration configuration)
    {
        var raw = configuration["Otel:Protocol"] ?? configuration["OTEL_EXPORTER_OTLP_PROTOCOL"];

        // Values follow the OTLP spec's protocol names, not the .NET enum spelling.
        return raw?.Trim().ToLowerInvariant() switch
        {
            "http/protobuf" or "httpprotobuf" => OtlpExportProtocol.HttpProtobuf,
            _ => OtlpExportProtocol.Grpc
        };
    }
}
