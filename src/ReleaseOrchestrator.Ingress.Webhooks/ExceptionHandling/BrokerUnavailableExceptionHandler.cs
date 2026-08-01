using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client.Exceptions;
using Rebus.Exceptions;

namespace ReleaseOrchestrator.Ingress.Webhooks.ExceptionHandling;

/// <summary>
/// Answers 503 instead of 500 when a webhook cannot be published because RabbitMQ is unreachable.
/// </summary>
/// <remarks>
/// <para>
/// The distinction is the whole point. 500 says "this server is broken" and senders treat it as
/// permanent — GitLab does not re-deliver, so the event is gone for good. 503 says "temporarily
/// unavailable, come back", which is both the honest description of a downed broker and the only
/// answer that leaves the event recoverable, whether by the sender's retry or by an operator
/// re-sending from the hook log.
/// </para>
/// <para>
/// An in-memory buffer ahead of the 503 was considered and rejected, and the README's Known
/// Limitations now records that rather than promising one. Buffering acknowledges the webhook with
/// 200 while the event exists only in RAM, so the sender
/// marks it delivered and a pod restart loses it with nobody the wiser. That trades a loud,
/// recoverable failure for a silent, permanent one — the exact defect this handler fixes. Not
/// losing events across a broker outage needs a persistent outbox, which is its own task.
/// </para>
/// </remarks>
public sealed class BrokerUnavailableExceptionHandler(
    ILogger<BrokerUnavailableExceptionHandler> logger) : IExceptionHandler
{
    /// <summary>Long enough to outlast a broker restart, short enough that a sender still retries.</summary>
    private const int RetryAfterSeconds = 30;

    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        if (!IsBrokerUnavailable(exception))
            return false;

        logger.LogError(
            exception,
            "Publishing a webhook to the broker failed; answering 503 so {Path} is re-delivered.",
            httpContext.Request.Path);

        httpContext.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        httpContext.Response.Headers.RetryAfter = RetryAfterSeconds.ToString();

        await httpContext.Response.WriteAsJsonAsync(
            new { error = "The event bus is unavailable. Retry this delivery." }, cancellationToken);

        return true;
    }

    /// <summary>
    /// Recognises a broker outage. Rebus sends through the RabbitMQ client, whose connection
    /// failures are <see cref="BrokerUnreachableException"/> when it cannot connect and the broader
    /// <see cref="OperationInterruptedException"/> (which <c>AlreadyClosedException</c> derives
    /// from) when the connection drops mid-publish; a broker that accepts TCP but never completes
    /// the handshake surfaces as a timeout instead. Rebus may wrap any of these in a
    /// <see cref="RebusApplicationException"/>, so the inner chain is walked.
    /// </summary>
    private static bool IsBrokerUnavailable(Exception exception) => exception switch
    {
        BrokerUnreachableException => true,
        OperationInterruptedException => true,
        RebusApplicationException => true,
        TimeoutException => true,
        _ => exception.InnerException is not null && IsBrokerUnavailable(exception.InnerException)
    };
}
