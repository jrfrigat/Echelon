using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;
using ReleaseOrchestrator.Core.Enums;

namespace ReleaseOrchestrator.Infrastructure.Persistence.Models;

/// <summary>A merge request, as read from its VCS.</summary>
// Natural key, unique: consumers upsert by check-then-insert, which races with itself under
// at-least-once delivery and multiple replicas.
[Index(nameof(RepositoryId), nameof(ExternalId), IsUnique = true, Name = "IX_MergeRequest_RepositoryId_ExternalId")]
// Linking an MR to a task that arrives later is a lookup on this.
[Index(nameof(TaskExternalId), Name = "IX_MergeRequest_TaskExternalId")]
[Index(nameof(RepositoryId), nameof(Status), Name = "IX_MergeRequest_RepositoryId_Status")]
[Index(nameof(TaskId), Name = "IX_MergeRequest_TaskId")]
// The planner filters on Status alone; RepositoryId leading means that index cannot seek for it.
[Index(nameof(Status), Name = "IX_MergeRequest_Status")]
[Index(nameof(MergedAt), Name = "IX_MergeRequest_MergedAt")]
[Index(nameof(ClosedAt), Name = "IX_MergeRequest_ClosedAt")]
public class MergeRequest
{
    /// <summary>Primary key.</summary>
    [Key]
    public Guid Id { get; set; }

    /// <summary>The VCS's own id for this merge request, unique within its repository.</summary>
    [Required, MaxLength(200)]
    public string ExternalId { get; set; } = string.Empty;

    /// <summary>Branch the work is on. The issue key is parsed out of it.</summary>
    [Required, MaxLength(500)]
    public string SourceBranch { get; set; } = string.Empty;

    /// <summary>Branch it merges into.</summary>
    [Required, MaxLength(500)]
    public string TargetBranch { get; set; } = string.Empty;

    /// <summary>The repository this belongs to.</summary>
    public Guid RepositoryId { get; set; }

    /// <summary>Its task, once one is known.</summary>
    public Guid? TaskId { get; set; }

    /// <summary>
    /// The issue key parsed from <see cref="SourceBranch"/>, kept whether or not the task exists.
    ///
    /// Events arrive in no particular order, so an MR routinely references a task that has not
    /// been imported yet. Without recording the key, such an MR is stored with no task and nothing
    /// ever links it: the branch would have to be re-parsed across the whole table to find it
    /// again. With the key, linking is a lookup once the task lands.
    /// </summary>
    [MaxLength(200)]
    public string? TaskExternalId { get; set; }

    /// <summary>
    /// Where it stands. Stored as text — see <see cref="AppDbContext.OnModelCreating"/>, which is
    /// also the only mapping here that no attribute can express.
    /// </summary>
    public MergeRequestStatus Status { get; set; }

    /// <summary>When the VCS opened it.</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>When it merged, if it did.</summary>
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

    /// <summary>Concurrency token. Two replicas can process the same webhook at once.</summary>
    [Timestamp]
    public byte[]? RowVersion { get; set; }

    /// <summary>The repository. Restrict: a repository with merge requests is not deleted out from under them.</summary>
    [ForeignKey(nameof(RepositoryId))]
    [InverseProperty(nameof(Models.Repository.MergeRequests))]
    [DeleteBehavior(DeleteBehavior.Restrict)]
    public Repository Repository { get; set; } = null!;

    /// <summary>Its task. SetNull: archiving a task leaves the merge request, unlinked.</summary>
    [ForeignKey(nameof(TaskId))]
    [InverseProperty(nameof(TaskItem.MergeRequests))]
    [DeleteBehavior(DeleteBehavior.SetNull)]
    public TaskItem? Task { get; set; }

    /// <summary>Every plan stage this merge request sits in.</summary>
    [InverseProperty(nameof(StageItem.MergeRequest))]
    public ICollection<StageItem> StageItems { get; set; } = [];
}
