namespace ReleaseOrchestrator.Infrastructure.Archive;

/// <summary>
/// Tunables for the nightly archive cycle: what counts as cold, how much moves at a time, and how
/// long the journals are kept.
/// </summary>
public class ArchiveOptions
{
    /// <summary>Whether the nightly cycle runs at all. Off leaves every row in the operational database.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Hour of day, UTC, at which the nightly cycle starts. Replaces the former
    /// <c>ScheduleCron</c> setting: no cron parser is available to this assembly, so the
    /// expression was bound and then ignored while the schedule stayed hard-coded. A narrow
    /// setting that is honoured beats a general one that silently does nothing.
    /// </summary>
    public int RunAtUtcHour { get; set; } = 2;

    /// <summary>
    /// How long a merge request or task must have been terminal before it is eligible to move.
    /// Measured from the merge, close or task-closed timestamp, never from first sight.
    /// </summary>
    public int ArchiveAfterDays { get; set; } = 90;

    /// <summary>Tasks moved per batch. One batch is one unit of work, retried and skipped as a whole.</summary>
    public int TaskBatchSize { get; set; } = 1000;

    /// <summary>Merge requests moved per batch. Smaller than the task batch: each row copies more columns.</summary>
    public int MrBatchSize { get; set; } = 500;

    /// <summary>
    /// How long merge-request status transitions are kept, in days. Two years by default.
    /// </summary>
    /// <remarks>
    /// Pruned rather than archived: these rows are the raw history a task timeline reads, and
    /// copying them into a third database to satisfy symmetry would buy nothing anyone reads.
    /// Because the journal has no foreign key, pruning it and archiving merge requests are
    /// independent in both directions — neither can block or corrupt the other, and a long-lived
    /// task can lose its earliest transitions while the task itself is still open. That is the
    /// trade a flat cutoff makes; raise this if a task's whole history matters more than the rows.
    /// </remarks>
    public int StatusJournalRetentionDays { get; set; } = 730;
}
