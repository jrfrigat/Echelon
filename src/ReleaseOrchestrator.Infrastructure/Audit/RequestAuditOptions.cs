namespace ReleaseOrchestrator.Infrastructure.Audit;

/// <summary>
/// Settings for HTTP request auditing.
/// </summary>
/// <remarks>
/// Plain settable properties with defaults and no validation attributes, matching
/// <c>ArchiveOptions</c>. Out-of-range values are clamped where they are used rather than thrown at
/// startup: a mistyped batch size should not stop the service from booting.
/// </remarks>
public class RequestAuditOptions
{
    /// <summary>Whether requests are recorded at all.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// How many records may wait to be written before new ones are dropped.
    /// </summary>
    /// <remarks>
    /// Bounded on purpose. An unbounded queue turns a database outage into unbounded memory growth,
    /// which takes the service down instead of the audit.
    /// </remarks>
    public int BufferCapacity { get; set; } = 10_000;

    /// <summary>How many records are written per database round trip.</summary>
    public int BatchSize { get; set; } = 200;

    /// <summary>How long a partial batch waits before being written anyway.</summary>
    public int FlushIntervalSeconds { get; set; } = 5;

    /// <summary>
    /// Ceiling on records from unauthenticated callers per minute.
    /// </summary>
    /// <remarks>
    /// The only thing standing between this table and an anonymous stranger. Requests that match no
    /// endpoint still reach the app shell and answer 200, so without a cap anyone on the internet
    /// could write rows here as fast as they can issue requests. Past the cap the record is counted
    /// and discarded, which costs one increment instead of a row plus three index entries — and the
    /// count is shown in the UI, so the gap is visible rather than silent.
    /// </remarks>
    public int MaxAnonymousRecordsPerMinute { get; set; } = 600;

    /// <summary>Whether caller IP addresses are recorded at all.</summary>
    public bool RecordClientIp { get; set; } = true;

    /// <summary>Whether caller display names are recorded. The one deliberately personal field.</summary>
    public bool RecordUserName { get; set; } = true;

    /// <summary>How long ordinary traffic is kept, in days.</summary>
    public int RetainDays { get; set; } = 14;

    /// <summary>
    /// How long failures and state changes are kept, in days.
    /// </summary>
    /// <remarks>
    /// Longer than <see cref="RetainDays"/> because routine successful reads are what costs the
    /// volume and what nobody goes back to look at, while a 500 or a delete is exactly what somebody
    /// asks about weeks later.
    /// </remarks>
    public int RetainNotableDays { get; set; } = 90;

    /// <summary>How often the pruner runs, in minutes.</summary>
    /// <remarks>
    /// Hourly rather than nightly so each pass deletes a small slice. A nightly pass on this volume
    /// deletes a very large number of rows in one sitting, which is exactly when a delete becomes a
    /// lock problem.
    /// </remarks>
    public int PruneIntervalMinutes { get; set; } = 60;

    /// <summary>How many rows are deleted per batch.</summary>
    public int PruneBatchSize { get; set; } = 5_000;

    /// <summary>
    /// Row count past which the pruner runs even without the coordination lease.
    /// </summary>
    /// <remarks>
    /// The lease fails closed when its backend is unreachable, so a long outage would otherwise stop
    /// pruning while writing continued. Deletes here are idempotent and batched, so a double run is
    /// harmless — an unbounded table is not.
    /// </remarks>
    public long HardRowCap { get; set; } = 20_000_000;

    /// <summary>How many rows a percentile calculation may sample before it reports itself approximate.</summary>
    public int SummarySampleCap { get; set; } = 20_000;
}
