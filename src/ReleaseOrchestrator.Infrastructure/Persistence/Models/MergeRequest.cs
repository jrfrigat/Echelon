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
    /// True when an operator set <see cref="Status"/> by hand. Status resolution then leaves it
    /// alone, so a later observation cannot silently undo a deliberate decision. Terminal states
    /// reported by the VCS (merged/closed) still win and clear the flag.
    /// </summary>
    public bool IsStatusManual { get; set; }

    /// <summary>
    /// The merge request's current labels, in canonical form: normalized, de-duplicated, sorted and
    /// comma-joined by <see cref="Core.Parsing.LabelSet.Canonical"/>. Empty string when it has none.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is what a per-environment readiness gate consults: a merge request labelled
    /// <c>ready-for-prod</c> satisfies the production environment's rule, and the labels have to be
    /// stored because the gate runs at plan and dispatch time, long after the webhook that carried
    /// them. Kept as one canonical string rather than a child table on purpose: the gate loads the
    /// merge request and checks its labels in memory (<see cref="Core.Parsing.ReadinessResolver"/>),
    /// nothing needs a per-label SQL query, and a plain column is copied by archiving and pruned by
    /// deletion for free — a child table would need a cascade the archive's direct delete does not run.
    /// </para>
    /// <para>
    /// Overwritten in place on each observation, so the transitions live in
    /// <see cref="MergeRequestLabelChange"/> exactly as status transitions live in their journal.
    /// Deliveries are unordered and carry no reliable label timestamp, so this is last-writer-wins;
    /// an out-of-order delivery that regresses the set is corrected by the next observation.
    /// </para>
    /// </remarks>
    [Required, MaxLength(2000)]
    public string Labels { get; set; } = string.Empty;

    /// <summary>
    /// The latest CI pipeline result for the merge request's head, as the provider reports it (e.g.
    /// <c>success</c>, <c>failed</c>, <c>running</c>), or null when none is known.
    /// </summary>
    /// <remarks>
    /// The other readiness signal a rule can require (<c>pipeline:success</c>). Stored like
    /// <see cref="Labels"/> and for the same reason — the gate reads it at launch, long after the
    /// observation — and populated only by the API-reading paths (reconcile and poll), since a
    /// merge-request webhook does not carry pipeline status; null there is "no information", never a
    /// reason to clear a result already known. See <see cref="Core.Parsing.ReadinessSignals"/>.
    /// </remarks>
    [MaxLength(50)]
    public string? PipelineResult { get; set; }

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
}
