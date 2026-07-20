using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using ReleaseOrchestrator.Application.Exceptions;
using ReleaseOrchestrator.Web.Resources;

namespace ReleaseOrchestrator.Web.ExceptionHandling;

/// <summary>
/// Maps domain failures to the status codes the controllers already advertise.
/// Without this every "not found" and every malformed YAML surfaced as a 500, and in
/// Development the full stack trace — table names included — went to the client.
/// </summary>
public class DomainExceptionHandler(
    ILogger<DomainExceptionHandler> logger,
    IStringLocalizer<ApiStrings> localizer) : IExceptionHandler
{
    /// <inheritdoc />
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext, Exception exception, CancellationToken ct)
    {
        var (status, titleKey) = exception switch
        {
            NotFoundException => (StatusCodes.Status404NotFound, "Error_NotFound_Title"),
            DomainValidationException => (StatusCodes.Status400BadRequest, "Error_Validation_Title"),
            _ => (0, string.Empty)
        };

        if (status == 0) return false;   // Not ours; let the default pipeline log and 500 it.

        // The log keeps the invariant key, not the localized title: logs are read by operators and
        // must stay greppable regardless of who happened to make the request.
        logger.LogInformation(
            "{Title}: {Message} [{RequestId}]", titleKey, exception.Message, httpContext.TraceIdentifier);

        var title = localizer[titleKey];

        var problem = new ProblemDetails
        {
            Status = status,
            Title = title,
            Detail = exception.Message,
            Instance = httpContext.Request.Path
        };

        // The one identifier that ties what the user saw to what the log recorded. Without it an
        // operator holding a screenshot of a 404 has nothing to search Seq by -- a repo-wide grep
        // for a correlation id previously returned nothing at all.
        problem.Extensions["traceId"] = httpContext.TraceIdentifier;

        httpContext.Response.StatusCode = status;
        await httpContext.Response.WriteAsJsonAsync(problem, ct);
        return true;
    }
}
