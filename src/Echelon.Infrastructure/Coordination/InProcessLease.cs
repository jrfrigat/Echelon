using Microsoft.Extensions.Logging;
using Echelon.Application.Services;

namespace Echelon.Infrastructure.Coordination;

/// <summary>
/// A lease held in this process, for a deployment that is one process.
/// </summary>
/// <remarks>
/// <para>
/// Not a stub. <see cref="IDistributedLease"/> exists so that background work registered in every
/// replica runs once per pass rather than N times; with one replica, "once across every replica"
/// and "once in this process" are the same statement, and this implements it exactly - including
/// the part that matters, which is refusing a second caller while the first still holds it.
/// </para>
/// <para>
/// It is wrong for two replicas, and it cannot detect that it is: every process would hold its own
/// lease and believe it leads. That is why the composition root refuses to select this provider
/// unless an operator says the deployment is a single instance. What they are asserting is a fact
/// about their deployment, and nothing here can check it for them.
/// </para>
/// <para>
/// Expiry is honoured rather than assumed away: work that overruns its lease duration must lose it
/// here for the same reason it would against Redis, or a single-instance deployment would be the
/// one place a stuck cycle blocks the next one forever.
/// </para>
/// </remarks>
internal sealed class InProcessLease(TimeProvider clock, ILogger<InProcessLease> logger) : IDistributedLease
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, DateTimeOffset> _held = new(StringComparer.Ordinal);

    /// <inheritdoc/>
    public Task<IAsyncDisposable?> TryAcquireAsync(string name, TimeSpan duration, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var now = clock.GetUtcNow();

        lock (_gate)
        {
            if (_held.TryGetValue(name, out var expiresAt) && expiresAt > now)
            {
                logger.LogDebug("Lease {Lease} is already held in this process until {ExpiresAt}.", name, expiresAt);
                return Task.FromResult<IAsyncDisposable?>(null);
            }

            _held[name] = now + duration;
        }

        return Task.FromResult<IAsyncDisposable?>(new Handle(this, name));
    }

    private void Release(string name)
    {
        lock (_gate) _held.Remove(name);
    }

    private sealed class Handle(InProcessLease owner, string name) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            owner.Release(name);
            return ValueTask.CompletedTask;
        }
    }
}
