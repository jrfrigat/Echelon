using Echelon.Application.DTOs;

namespace Echelon.Application.Services;

/// <summary>
/// Accepts request records from the hot path.
/// </summary>
/// <remarks>
/// <para>
/// The single member returns <c>void</c>, not <c>Task</c>, and that is a design statement rather
/// than an oversight: a request must never wait on the audit. There is no signature here that lets a
/// caller await a database write while a user is waiting for their response, so nobody can add one
/// later without changing this interface and explaining why.
/// </para>
/// <para>
/// The implementation buffers and never blocks. When the buffer is full it drops, counts the drop,
/// and reports the count - an audit that slows the service down to stay complete has the trade
/// backwards.
/// </para>
/// </remarks>
public interface IRequestAuditSink
{
    /// <summary>
    /// Offers a record for storage. Never blocks, never throws.
    /// </summary>
    /// <param name="record">The completed request.</param>
    void Enqueue(RequestAuditRecord record);
}
