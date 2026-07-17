using Rebus.Bus;
using Rebus.Bus.Advanced;

namespace ReleaseOrchestrator.UnitTests.Queue;

/// <summary>
/// An <see cref="IBus"/> that records what a handler sends, and does nothing else.
/// </summary>
/// <remarks>
/// The handlers are plain classes now — <c>Handle(message)</c> with an injected <see cref="IBus"/> —
/// so a test drives one by calling it directly and reading back what it forwarded, with no broker
/// and no pipeline. This records <see cref="Send"/> and <see cref="Publish"/>, which is all the
/// handlers use; every other bus operation throws, so a handler reaching for one that these tests
/// do not model fails loudly rather than silently doing nothing.
/// </remarks>
internal sealed class RecordingBus : IBus
{
    private readonly List<object> _sent = [];

    /// <summary>Everything sent, in order.</summary>
    public IReadOnlyList<object> Sent => _sent;

    /// <summary>The single message of type <typeparamref name="T"/> that was sent.</summary>
    public T SingleSent<T>() => _sent.OfType<T>().Single();

    /// <summary>The messages of type <typeparamref name="T"/> that were sent.</summary>
    public IReadOnlyList<T> AllSent<T>() => [.. _sent.OfType<T>()];

    /// <summary>True when at least one message of type <typeparamref name="T"/> was sent.</summary>
    public bool AnySent<T>() => _sent.OfType<T>().Any();

    public Task Send(object commandMessage, IDictionary<string, string>? optionalHeaders = null)
    {
        _sent.Add(commandMessage);
        return Task.CompletedTask;
    }

    public Task Publish(object eventMessage, IDictionary<string, string>? optionalHeaders = null)
    {
        _sent.Add(eventMessage);
        return Task.CompletedTask;
    }

    public Task SendLocal(object commandMessage, IDictionary<string, string>? optionalHeaders = null) =>
        throw new NotSupportedException("A handler under test used SendLocal; RecordingBus models only Send/Publish.");

    public Task Defer(TimeSpan delay, object message, IDictionary<string, string>? optionalHeaders = null) =>
        throw new NotSupportedException("A handler under test used Defer; RecordingBus models only Send/Publish.");

    public Task DeferLocal(TimeSpan delay, object message, IDictionary<string, string>? optionalHeaders = null) =>
        throw new NotSupportedException("A handler under test used DeferLocal; RecordingBus models only Send/Publish.");

    public Task Reply(object replyMessage, IDictionary<string, string>? optionalHeaders = null) =>
        throw new NotSupportedException("A handler under test used Reply; RecordingBus models only Send/Publish.");

    public Task Subscribe(Type eventType) => throw new NotSupportedException();
    public Task Subscribe<TEvent>() => throw new NotSupportedException();
    public Task Unsubscribe(Type eventType) => throw new NotSupportedException();
    public Task Unsubscribe<TEvent>() => throw new NotSupportedException();

    public IAdvancedApi Advanced => throw new NotSupportedException();

    public void Dispose() { }
}
