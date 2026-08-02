using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace ReleaseOrchestrator.Infrastructure.Persistence.Models;

/// <summary>
/// One step in an operator's explicit ordering of a task's prerequisites.
/// </summary>
/// <remarks>
/// <para>
/// A preference over the prerequisites that already exist, never a way to create one: the planner
/// drops an entry naming a task the wait policy did not admit, so turning off "wait for subtasks"
/// cannot be undone by a stale sequence that still lists one.
/// </para>
/// <para>
/// Stored per task rather than per plan, like the wait policy and for the same reason — a plan is
/// regenerated on every ingestion event, so anything held against a plan's id is orphaned by the
/// next recalculation.
/// </para>
/// </remarks>
// One position per prerequisite within a task, and one entry per prerequisite: both directions are
// nonsense if duplicated, and the unique indexes are what stop a half-applied reorder leaving two
// tasks claiming the same slot.
[Index(nameof(TaskId), nameof(PrerequisiteTaskId), IsUnique = true, Name = "IX_TaskPrerequisiteOrder_Task_Prerequisite")]
[Index(nameof(TaskId), nameof(Position), IsUnique = true, Name = "IX_TaskPrerequisiteOrder_Task_Position")]
public class TaskPrerequisiteOrder
{
    /// <summary>Primary key.</summary>
    [Key]
    public Guid Id { get; set; }

    /// <summary>The task whose prerequisites are being ordered.</summary>
    public Guid TaskId { get; set; }

    /// <summary>The prerequisite occupying this position.</summary>
    public Guid PrerequisiteTaskId { get; set; }

    /// <summary>Zero-based position in the sequence. Lower deploys first.</summary>
    public int Position { get; set; }

    /// <summary>The ordering task. Cascade: the sequence is meaningless without it.</summary>
    [ForeignKey(nameof(TaskId))]
    [InverseProperty(nameof(TaskItem.PrerequisiteOrder))]
    public TaskItem Task { get; set; } = null!;

    /// <summary>
    /// The prerequisite. Restrict, and deliberately not a second cascade: SQL Server rejects two
    /// cascade paths into one table, and a prerequisite disappearing from under a sequence should
    /// block rather than silently renumber it.
    /// </summary>
    [ForeignKey(nameof(PrerequisiteTaskId))]
    [DeleteBehavior(DeleteBehavior.Restrict)]
    public TaskItem PrerequisiteTask { get; set; } = null!;
}
