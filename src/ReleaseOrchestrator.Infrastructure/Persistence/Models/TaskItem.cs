using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;
using ReleaseOrchestrator.Core.Enums;

namespace ReleaseOrchestrator.Infrastructure.Persistence.Models;

/// <summary>A task imported from an issue tracker.</summary>
// Natural key, unique: consumers upsert by check-then-insert, which races with itself under
// at-least-once delivery and multiple replicas. Issue keys are per-tracker, never global.
[Index(nameof(TrackerConnectionId), nameof(ExternalId), IsUnique = true, Name = "IX_TaskItem_TrackerConnectionId_ExternalId")]
[Index(nameof(ExternalId), Name = "IX_TaskItem_ExternalId")]
[Index(nameof(ClosedAt), Name = "IX_TaskItem_ClosedAt")]
public class TaskItem
{
    /// <summary>Primary key.</summary>
    [Key]
    public Guid Id { get; set; }

    /// <summary>The tracker's issue key, e.g. <c>PROJ-1</c>. Unique only within its tracker.</summary>
    [Required, MaxLength(200)]
    public string ExternalId { get; set; } = string.Empty;

    /// <summary>
    /// The parent task in the tracker's hierarchy (an epic over its subtasks), if one is set. Null
    /// for a top-level task.
    /// </summary>
    /// <remarks>
    /// Whether a parent waits on its children is a policy, not a given: see
    /// <see cref="WaitForSubtasks"/>. When it does, the planner treats the hierarchy as one more
    /// task-dependency edge (parent depends on child). Stored as a scalar because that is how the
    /// tracker reports it: an issue names its one parent, not the parent its children. The inverse is
    /// <see cref="Children"/>.
    /// </remarks>
    public Guid? ParentTaskId { get; set; }

    /// <summary>
    /// This task's answer to "wait for my subtasks?", or null to inherit the installation default.
    /// </summary>
    /// <remarks>
    /// Null is not false, and the distinction is the whole point of the column: it is what lets the
    /// global default change and carry every task that never disagreed with it. See
    /// <c>TaskWaitPolicy</c>, which resolves the two.
    /// </remarks>
    public bool? WaitForSubtasks { get; set; }

    /// <summary>
    /// This task's answer to "wait for the tasks I declare a dependency on?", or null to inherit.
    /// </summary>
    public bool? WaitForLinked { get; set; }

    /// <summary>
    /// This task's answer to whether one whole group of prerequisites precedes the other, or null to
    /// inherit. See <see cref="PrerequisiteGroupOrder"/>.
    /// </summary>
    public PrerequisiteGroupOrder? PrerequisiteGroupOrder { get; set; }

    /// <summary>An operator's explicit ordering over this task's prerequisites, if one was set.</summary>
    [InverseProperty(nameof(TaskPrerequisiteOrder.Task))]
    public ICollection<TaskPrerequisiteOrder> PrerequisiteOrder { get; set; } = [];

    /// <summary>
    /// Operator edits to this task's plan — added or dropped ordering edges between merge requests.
    /// </summary>
    /// <remarks>
    /// Held on the task rather than on the plan because a plan is regenerated on every ingestion
    /// event: an edit stored against one plan's id would be orphaned by the next recalculation, which
    /// is exactly why hand ordering never survived. As an input it survives by construction.
    /// </remarks>
    [InverseProperty(nameof(PlanOverride.Task))]
    public ICollection<PlanOverride> PlanOverrides { get; set; } = [];

    /// <summary>Issue summary, for operators reading the plan.</summary>
    [Required, MaxLength(500)]
    public string Title { get; set; } = string.Empty;

    /// <summary>The tracker's own status key. Only its adapter knows which values mean closed.</summary>
    [Required, MaxLength(100)]
    public string Status { get; set; } = string.Empty;

    /// <summary>When the task reached a closed status. Archiving keys off this.</summary>
    public DateTime? ClosedAt { get; set; }

    /// <summary>
    /// When this service first stored the task. Null for tasks that predate the column.
    /// </summary>
    /// <remarks>
    /// Written on insert only, and deliberately not backfilled: "we do not know when this arrived"
    /// has to stay distinguishable from a real timestamp, or the history quietly claims every
    /// pre-existing task arrived on the day this shipped.
    /// </remarks>
    public DateTime? FirstSeenAt { get; set; }

    /// <summary>
    /// How it arrived: the ingestion source, or a marker for the paths that are not arrivals.
    /// </summary>
    /// <remarks>
    /// Null where the creating path knows of no source — rendered as "arrival channel not recorded".
    /// It is never filled in from the tracker connection instead, tempting as that is: a task
    /// created because a merge-request branch mentioned its key would then read as having been
    /// announced by the tracker, which is a webhook that never happened.
    /// </remarks>
    [MaxLength(200)]
    public string? FirstSeenSource { get; set; }

    /// <summary>The tracker it came from.</summary>
    public Guid TrackerConnectionId { get; set; }

    /// <summary>Concurrency token. Two replicas can process the same webhook at once.</summary>
    [Timestamp]
    public byte[]? RowVersion { get; set; }

    /// <summary>Its tracker. Restrict: a tracker with tasks is not deleted out from under them.</summary>
    [ForeignKey(nameof(TrackerConnectionId))]
    [InverseProperty(nameof(Models.TrackerConnection.Tasks))]
    [DeleteBehavior(DeleteBehavior.Restrict)]
    public TrackerConnection TrackerConnection { get; set; } = null!;

    /// <summary>
    /// Its parent task, if any. Restrict, and self-referencing: SQL Server rejects a cascade or
    /// set-null action that targets the same table, so a parent with children cannot be deleted
    /// out from under them — the archive drains children first (see <c>ArchiveRunner</c>).
    /// </summary>
    [ForeignKey(nameof(ParentTaskId))]
    [InverseProperty(nameof(Children))]
    [DeleteBehavior(DeleteBehavior.Restrict)]
    public TaskItem? ParentTask { get; set; }

    /// <summary>Its child tasks — the subtasks that deploy before it. Inverse of <see cref="ParentTask"/>.</summary>
    [InverseProperty(nameof(ParentTask))]
    public ICollection<TaskItem> Children { get; set; } = [];

    /// <summary>Tasks this one waits on — deploy those first.</summary>
    [InverseProperty(nameof(TaskDependency.DependentTask))]
    public ICollection<TaskDependency> Dependencies { get; set; } = [];

    /// <summary>Tasks waiting on this one — deploy this first.</summary>
    [InverseProperty(nameof(TaskDependency.DependsOnTask))]
    public ICollection<TaskDependency> Dependents { get; set; } = [];

    /// <summary>Merge requests whose branch named this task.</summary>
    [InverseProperty(nameof(MergeRequest.Task))]
    public ICollection<MergeRequest> MergeRequests { get; set; } = [];
}
