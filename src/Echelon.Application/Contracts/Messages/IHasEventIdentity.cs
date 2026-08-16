namespace Echelon.Application.Contracts.Messages;

/// <summary>
/// A message that carries a stable identity so it can be deduplicated.
/// </summary>
/// <remarks>
/// <c>Source</c> + <c>EventId</c> identify a distinct observation. Once a change can be seen by both
/// a webhook push and a poll, at-least-once delivery over two channels means the same event arrives
/// more than once; the inbox drops the repeats by that pair. This is the only part taken from
/// CloudEvents -- a full envelope would cost type safety for nothing dedup needs
///.
/// </remarks>
public interface IHasEventIdentity
{
    /// <summary>The producer, as <c>"{providerType}/{connectionName}"</c>.</summary>
    string Source { get; }

    /// <summary>The event id, unique within <see cref="Source"/> -- the provider's own id, or a deterministic hash.</summary>
    string EventId { get; }
}
