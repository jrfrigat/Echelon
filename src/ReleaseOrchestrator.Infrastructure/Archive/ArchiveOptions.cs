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
    public int PlanBatchSize { get; set; } = 100;
}
