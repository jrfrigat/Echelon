namespace ReleaseOrchestrator.Infrastructure.Archive;

public class ArchiveOptions
{
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Hour of day, UTC, at which the nightly cycle starts. Replaces the former
    /// <c>ScheduleCron</c> setting: no cron parser is available to this assembly, so the
    /// expression was bound and then ignored while the schedule stayed hard-coded. A narrow
    /// setting that is honoured beats a general one that silently does nothing.
    /// </summary>
    public int RunAtUtcHour { get; set; } = 2;

    public int ArchiveAfterDays { get; set; } = 90;
    public int TaskBatchSize { get; set; } = 1000;
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
