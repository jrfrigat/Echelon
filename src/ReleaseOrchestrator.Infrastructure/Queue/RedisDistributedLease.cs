using Microsoft.Extensions.Logging;
using ReleaseOrchestrator.Application.Services;
using StackExchange.Redis;

namespace ReleaseOrchestrator.Infrastructure.Queue;

/// <summary>
/// A lease over Redis: <c>SET key owner NX PX ttl</c>, renewed while held, released on dispose.
/// </summary>
/// <remarks>
/// <para>
/// Not a consensus algorithm. A single Redis is a single point of failure, and under a partition
/// two replicas could briefly believe they hold the same lease. That is acceptable for what it
/// guards: archiving and reconciliation are idempotent, so the worst case of a double run is the
/// work we already do today, every cycle, in every replica. The lease removes that as the normal
/// case rather than promising it can never happen.
/// </para>
/// <para>
/// Anything where a double run would be a correctness bug needs a real fencing token, not this.
/// </para>
/// </remarks>
public sealed class RedisDistributedLease(
    IConnectionMultiplexer redis,
    ILogger<RedisDistributedLease> logger) : IDistributedLease
{
    /// <inheritdoc/>
    public async Task<IAsyncDisposable?> TryAcquireAsync(string name, TimeSpan duration, CancellationToken ct)
    {
        var key = $"lease:{name}";

        // A per-attempt owner, so a lease can only ever be released or renewed by the exact holder.
        // Without it, a replica whose lease expired mid-run would delete the successor's on exit.
        var owner = Guid.NewGuid().ToString("N");

        try
        {
            var db = redis.GetDatabase();
            var acquired = await db.StringSetAsync(key, owner, duration, When.NotExists).ConfigureAwait(false);

            if (!acquired) return null;

            logger.LogDebug("Acquired lease {Lease}", name);
            return new Handle(db, key, owner, duration, logger);
        }
        catch (RedisConnectionException ex)
        {
            // Fail closed: no lease means no run. Redis being down is not a reason to have every
            // replica start deleting rows at once - the pass will happen on the next tick.
            logger.LogWarning(ex, "Cannot reach Redis to take lease {Lease}; skipping this pass", name);
            return null;
        }
    }

    private sealed class Handle : IAsyncDisposable
    {
        private static readonly TimeSpan RenewFailureBackoff = TimeSpan.FromSeconds(1);

        // Compare-and-delete / compare-and-renew. Redis has no primitive for "act only if I still
        // own this", and doing it as GET-then-act would race with the very expiry it guards.
        private const string ReleaseScript =
            "if redis.call('get', KEYS[1]) == ARGV[1] then return redis.call('del', KEYS[1]) else return 0 end";
        private const string RenewScript =
            "if redis.call('get', KEYS[1]) == ARGV[1] then return redis.call('pexpire', KEYS[1], ARGV[2]) else return 0 end";

        private readonly IDatabase _db;
        private readonly string _key;
        private readonly string _owner;
        private readonly TimeSpan _duration;
        private readonly ILogger _logger;
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _renewal;

        internal Handle(IDatabase db, string key, string owner, TimeSpan duration, ILogger logger)
        {
            _db = db;
            _key = key;
            _owner = owner;
            _duration = duration;
            _logger = logger;
            _renewal = RenewAsync(_stop.Token);
        }

        /// <summary>
        /// Extends the lease while the work runs, so the duration bounds "how long after a crash
        /// until someone else may start" rather than "how long the job is allowed to take".
        /// </summary>
        private async Task RenewAsync(CancellationToken ct)
        {
            var interval = _duration / 3;

            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(interval, ct).ConfigureAwait(false);

                    var renewed = (long)await _db.ScriptEvaluateAsync(
                        RenewScript, [_key], [_owner, (long)_duration.TotalMilliseconds]).ConfigureAwait(false);

                    if (renewed == 0)
                    {
                        // Someone else holds it now - this replica stalled past the expiry. Say so:
                        // it means two passes may be running, and the duration is too short.
                        _logger.LogWarning("Lost lease {Key} while still working; another replica may have started it", _key);
                        return;
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
                catch (RedisException ex)
                {
                    // Transient: keep trying until the lease would have expired anyway.
                    _logger.LogWarning(ex, "Could not renew lease {Key}", _key);
                    try { await Task.Delay(RenewFailureBackoff, ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { return; }
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync().ConfigureAwait(false);

            try
            {
                await _renewal.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected: cancellation is how the renewal loop is stopped.
            }

            try
            {
                await _db.ScriptEvaluateAsync(ReleaseScript, [_key], [_owner]).ConfigureAwait(false);
            }
            catch (RedisException ex)
            {
                // Not fatal: the lease expires on its own. Releasing early is an optimisation, so a
                // failure here costs one idle cycle, not correctness.
                _logger.LogWarning(ex, "Could not release lease {Key}; it will expire", _key);
            }

            _stop.Dispose();
        }
    }
}
