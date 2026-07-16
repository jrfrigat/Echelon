using ReleaseOrchestrator.Core.Enums;

namespace ReleaseOrchestrator.Core.Entities;

public class MergeRequest
{
    public Guid Id { get; set; }
    public string ExternalId { get; set; } = string.Empty;
    public string SourceBranch { get; set; } = string.Empty;
    public string TargetBranch { get; set; } = string.Empty;
    public Guid RepositoryId { get; set; }
    public Guid? TaskId { get; set; }

    /// <summary>
    /// The issue key parsed from <see cref="SourceBranch"/>, kept whether or not the task exists.
    ///
    /// Events arrive in no particular order, so an MR routinely references a task that has not
    /// been imported yet. Without recording the key, such an MR is stored with no task and nothing
    /// ever links it: the branch would have to be re-parsed across the whole table to find it
    /// again. With the key, linking is a lookup once the task lands.
    /// </summary>
    public string? TaskExternalId { get; set; }
    public MergeRequestStatus Status { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? MergedAt { get; set; }

    /// <summary>Set when the MR is closed without merging. Archiving needs both this
    /// and <see cref="MergedAt"/>: a closed MR never gets a merge timestamp.</summary>
    public DateTime? ClosedAt { get; set; }

    /// <summary>
    /// True when an operator set <see cref="Status"/> by hand. Label-driven promotion then
    /// leaves it alone, so a webhook cannot silently undo a deliberate decision. Terminal
    /// states reported by the VCS (merged/closed) still win and clear the flag.
    /// </summary>
    public bool IsStatusManual { get; set; }

    public byte[]? RowVersion { get; set; }

    public Repository Repository { get; set; } = null!;
    public TaskItem? Task { get; set; }
    public ICollection<StageItem> StageItems { get; set; } = [];
}
