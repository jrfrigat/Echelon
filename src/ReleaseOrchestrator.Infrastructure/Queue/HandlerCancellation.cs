using Rebus.Extensions;
using Rebus.Pipeline;

namespace ReleaseOrchestrator.Infrastructure.Queue;

/// <summary>
/// The cancellation token for the message being handled, or none when there is no message.
/// </summary>
/// <remarks>
/// Rebus hands a handler its message and nothing else; the cancellation token lives on the ambient
/// <see cref="MessageContext"/> that the pipeline sets around each dispatch. Reading it through here
/// rather than touching <see cref="MessageContext.Current"/> in every handler does one useful thing:
/// it is null-safe. A handler invoked outside the pipeline - a unit test calling <c>Handle</c>
/// directly - has no ambient context, and this returns <see cref="CancellationToken.None"/> instead
/// of throwing, so the handler stays a plain, directly-callable class. In the running service the
/// context is always present and the real token flows to the database calls, cancelling them when
/// Rebus is shutting down.
/// </remarks>
internal static class HandlerCancellation
{
    /// <summary>The current message's token, or <see cref="CancellationToken.None"/> outside a dispatch.</summary>
    public static CancellationToken Token =>
        MessageContext.Current is { } context ? context.GetCancellationToken() : CancellationToken.None;
}
