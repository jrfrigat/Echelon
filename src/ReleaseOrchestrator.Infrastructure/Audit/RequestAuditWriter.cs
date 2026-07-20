using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ReleaseOrchestrator.Application.DTOs;
using ReleaseOrchestrator.Infrastructure.Archive;
using ReleaseOrchestrator.Infrastructure.Archive.Entities;

namespace ReleaseOrchestrator.Infrastructure.Audit;

/// <summary>
/// Drains buffered request records into the archive database in batches.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately NOT leased, which inverts the convention every other background job here follows —
/// so it is worth saying why. A lease stops replicas duplicating SHARED work. This work is not
/// shared: each replica's buffer holds only its own requests, and leasing would silence every
/// replica but one, losing most of the traffic rather than deduplicating it.
/// </para>
/// <para>
/// A failed flush loses that batch and says so. The alternative — retrying indefinitely — turns a
/// database problem into unbounded memory growth in a component whose whole purpose is to be
/// harmless.
/// </para>
/// </remarks>
public class RequestAuditWriter(
    RequestAuditBuffer buffer,
    IServiceScopeFactory scopeFactory,
    IOptions<RequestAuditOptions> options,
    TimeProvider clock,
    ILogger<RequestAuditWriter> logger) : BackgroundService
{
    private long _lastReportedDrops;

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;

        if (!settings.Enabled)
        {
            logger.LogInformation("Request audit writer is disabled.");
            return;
        }

        var flushInterval = TimeSpan.FromSeconds(Math.Max(1, settings.FlushIntervalSeconds));
        var batchSize = Math.Max(1, settings.BatchSize);
        using var timer = new PeriodicTimer(flushInterval, clock);

        var pending = new List<RequestAuditRecord>(batchSize);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Fill up to a batch without waiting, then flush on the tick. Reading greedily first
                // keeps a burst from sitting in the buffer for a whole interval.
                while (pending.Count < batchSize && buffer.Reader.TryRead(out var record))
                    pending.Add(record);

                if (pending.Count >= batchSize)
                {
                    await FlushAsync(pending, stoppingToken);
                    continue;
                }

                await timer.WaitForNextTickAsync(stoppingToken);

                while (pending.Count < batchSize && buffer.Reader.TryRead(out var record))
                    pending.Add(record);

                if (pending.Count > 0)
                    await FlushAsync(pending, stoppingToken);

                ReportDrops();
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Shutdown. Write what is in hand rather than discarding it silently -- the host
                // allows a stop timeout for exactly this, and the alternative is losing up to a
                // full batch plus the channel's contents on every ordinary restart, invisibly.
                await FlushOnShutdownAsync(pending);
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Request audit flush failed; {Count} record(s) lost", pending.Count);
                // Counted, not just logged: a failed write leaves the same hole as a refused one, and
                // the summary must not keep reporting zero dropped across it.
                buffer.RecordLoss(pending.Count);
                pending.Clear();
            }
        }
    }

    /// <summary>
    /// Last-chance flush of the in-hand batch and whatever is still queued.
    /// </summary>
    /// <remarks>
    /// Uses its own short timeout rather than the (already cancelled) stopping token, which would
    /// abort the write immediately. Anything still unwritten after this is counted as lost, so a
    /// restart that did drop records says so instead of quietly shortening the history.
    /// </remarks>
    private async Task FlushOnShutdownAsync(List<RequestAuditRecord> pending)
    {
        try
        {
            while (pending.Count < Math.Max(1, options.Value.BatchSize) * 4 && buffer.Reader.TryRead(out var record))
                pending.Add(record);

            if (pending.Count == 0) return;

            using var grace = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await FlushAsync(pending, grace.Token);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Request audit could not flush {Count} record(s) during shutdown", pending.Count);
            buffer.RecordLoss(pending.Count);
        }
    }

    private async Task FlushAsync(List<RequestAuditRecord> pending, CancellationToken ct)
    {
        if (pending.Count == 0) return;

        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ArchiveDbContext>();

        foreach (var record in pending)
            db.RequestAuditEntries.Add(new RequestAuditEntry
            {
                // Time-ordered so this append-only table appends rather than fragmenting its index.
                Id = Guid.CreateVersion7(),
                Host = record.Host,
                Instance = record.Instance,
                Method = record.Method,
                RoutePattern = record.RoutePattern,
                Path = record.Path,
                Kind = record.Kind,
                StatusCode = record.StatusCode,
                DurationMs = record.DurationMs,
                StartedAt = record.StartedAt,
                UserId = record.UserId,
                UserName = record.UserName,
                PeerIp = record.PeerIp,
                ForwardedIp = record.ForwardedIp,
                Permission = record.Permission,
                CorrelationId = record.CorrelationId,
                ExceptionType = record.ExceptionType,
                IsNotable = record.IsNotable
            });

        await db.SaveChangesAsync(ct);
        pending.Clear();
    }

    /// <summary>
    /// Logs newly dropped records, naming the setting to raise.
    /// </summary>
    /// <remarks>
    /// Logged on the delta rather than the total, so a one-off burst does not repeat forever. The
    /// count also reaches the UI through the summary — a warning only visible to whoever reads the
    /// log is exactly the silent incompleteness this feature is supposed to avoid.
    /// </remarks>
    private void ReportDrops()
    {
        var dropped = buffer.Dropped;
        if (dropped <= _lastReportedDrops) return;

        logger.LogWarning(
            "Request audit dropped {Count} record(s) since the last report. Raise RequestAudit:BufferCapacity "
            + "or RequestAudit:MaxAnonymousRecordsPerMinute if this persists.",
            dropped - _lastReportedDrops);

        _lastReportedDrops = dropped;
    }
}
