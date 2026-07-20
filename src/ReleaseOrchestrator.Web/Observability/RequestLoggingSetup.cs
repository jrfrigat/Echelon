using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Serilog;
using Serilog.Events;

namespace ReleaseOrchestrator.Observability;

/// <summary>
/// Request logging with levels that match what the client actually got.
/// </summary>
/// <remarks>
/// <para>
/// Two defects this fixes, both of which made the log actively misleading rather than merely noisy.
/// </para>
/// <para>
/// First, <c>UseSerilogRequestLogging</c> sat downstream of <c>UseExceptionHandler</c>, so a domain
/// exception passed through the logging middleware — which recorded it as a 500 at Error — before the
/// handler converted it into the 404 or 400 the caller received. Every "task not found" in Seq read
/// as a server fault. Moving the logging above the handler makes it observe the response that was
/// actually sent.
/// </para>
/// <para>
/// Second, every level was Information, so a cold PWA load — hundreds of framework and ICU assets —
/// cost hundreds of Seq events, and a health probe on a timer cost one forever. Static assets and
/// infrastructure paths now log at Verbose, which is below the configured minimum and therefore
/// never leaves the process.
/// </para>
/// <para>
/// Lives in this folder because the Ingress project compiles it through a source glob, so it may
/// reference ASP.NET Core and Serilog but nothing from Infrastructure.
/// </para>
/// </remarks>
public static class RequestLoggingSetup
{
    /// <summary>
    /// Adds request logging that levels by outcome and stays quiet about infrastructure traffic.
    /// </summary>
    /// <param name="app">The application builder.</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <remarks>
    /// Register this ABOVE <c>UseExceptionHandler</c>. Below it, the status recorded here is the one
    /// the exception produced rather than the one the handler wrote, and nothing in the test suite
    /// would notice — there is no integration host in this repo.
    /// </remarks>
    public static IApplicationBuilder UseReleaseOrchestratorRequestLogging(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        return app.UseSerilogRequestLogging(options =>
        {
            options.GetLevel = (httpContext, _, exception) =>
            {
                // An exception that reached here unhandled is a real fault, whatever the path.
                if (exception is not null) return LogEventLevel.Error;

                // No matched endpoint means a static asset or a file that does not exist. Both are
                // noise: a single cold PWA load fetches hundreds of framework files.
                if (httpContext.GetEndpoint() is null) return LogEventLevel.Verbose;

                if (TelemetryExtensions.IsInfrastructureRequest(httpContext.Request.Path))
                    return LogEventLevel.Verbose;

                return httpContext.Response.StatusCode switch
                {
                    >= 500 => LogEventLevel.Error,
                    // Client errors are worth seeing but are not faults: a 404 for a mistyped id and
                    // a 403 for a missing grant are both the system working as designed.
                    >= 400 => LogEventLevel.Warning,
                    _ => LogEventLevel.Information
                };
            };

            options.EnrichDiagnosticContext = (diagnosticContext, httpContext) =>
                // The id the response carried, so a report of "I saw this error" can be found.
                diagnosticContext.Set("RequestId", httpContext.TraceIdentifier);
        });
    }
}
