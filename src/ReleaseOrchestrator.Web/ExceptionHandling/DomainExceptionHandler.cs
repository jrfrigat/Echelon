using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using ReleaseOrchestrator.Application.Exceptions;

namespace ReleaseOrchestrator.Web.ExceptionHandling;

/// <summary>
/// Maps domain failures to the status codes the controllers already advertise.
/// Without this every "not found" and every malformed YAML surfaced as a 500, and in
/// Development the full stack trace — table names included — went to the client.
/// </summary>
public class DomainExceptionHandler(ILogger<DomainExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext, Exception exception, CancellationToken ct)
    {
        var (status, title) = exception switch
        {
            NotFoundException => (StatusCodes.Status404NotFound, "Not found"),
            DomainValidationException => (StatusCodes.Status400BadRequest, "Invalid request"),
            _ => (0, string.Empty)
        };

        if (status == 0) return false;   // Not ours; let the default pipeline log and 500 it.

        logger.LogInformation("{Title}: {Message}", title, exception.Message);

        var problem = new ProblemDetails
        {
            Status = status,
            Title = title,
            Detail = exception.Message,
            Instance = httpContext.Request.Path
        };

        httpContext.Response.StatusCode = status;
        await httpContext.Response.WriteAsJsonAsync(problem, ct);
        return true;
    }
}
