using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;

namespace Echelon.Infrastructure.Persistence.Models;

/// <summary>
/// One recorded transition of a merge request's status: what it was, what it became, what caused it,
/// and who - when anybody did.
/// </summary>
/// <remarks>
/// <para>
/// The one part of a task's history that cannot be derived. <see cref="MergeRequest.Status"/> is
/// overwritten in place, so the promotion to <c>ReadyForDeploy</c> - the moment a merge request
/// became deployable - leaves no trace: no column, no timestamp, nothing to read afterwards.
/// (Plan membership is a separate, coarser thing: the plan holds every non-closed merge request the
/// task owns, and the per-environment readiness gate decides at launch which may deploy.)
/// <c>CreatedAt</c>, <c>MergedAt</c> and <c>ClosedAt</c> cover three moments; everything between them
/// was invisible.
/// </para>
/// <para>
/// Deliberately has NO foreign key and NO navigation on <see cref="MergeRequest"/>. An FK would
/// force a choice between two bad outcomes: cascade, and archiving a merge request destroys the
/// history of what happened to it; or restrict, and the archive's <c>ExecuteDeleteAsync</c>
/// FK-violates and wedges the whole batch, which is exactly the failure the existing archive gates
/// exist to prevent. Independence in both directions is the point - the journal is pruned on its own
/// schedule and neither operation can break the other.
/// </para>
/// <para>
/// The consequence of having no FK is that the subject must be denormalised: after the merge request
/// row is hard-deleted, <see cref="MergeRequestExternalId"/> is the only thing left that names what
/// the entry is about. <c>ArchivedMergeRequest.TaskExternalId</c> already does the same for the same
/// reason.
/// </para>
/// </remarks>
[Index(nameof(TaskId), nameof(At), Name = "IX_MergeRequestStatusChange_TaskId_At")]
[Index(nameof(MergeRequestId), nameof(At), Name = "IX_MergeRequestStatusChange_MergeRequestId_At")]
public class MergeRequestStatusChange
{
    /// <summary>Primary key.</summary>
    [Key]
    public Guid Id { get; set; }

    /// <summary>The merge request that changed. A plain id: there is no FK, deliberately.</summary>
    public Guid MergeRequestId { get; set; }

    /// <summary>The task it belonged to at the time, so a task's history can be read without a join.</summary>
    public Guid? TaskId { get; set; }

    /// <summary>The merge request's own key, kept so the entry still names its subject after the row is gone.</summary>
    [MaxLength(128)]
    public string? MergeRequestExternalId { get; set; }

    /// <summary>The status before the change. Null when the merge request was created in this state.</summary>
    [MaxLength(50)]
    public string? FromStatus { get; set; }

    /// <summary>The status after the change.</summary>
    [Required, MaxLength(50)]
    public string ToStatus { get; set; } = string.Empty;

    /// <summary>
    /// What drove it: <c>webhook</c>, <c>poll</c>, <c>label</c> or <c>manual-pin</c>.
    /// </summary>
    /// <remarks>
    /// Worth recording separately from the actor: three of these four have no actor at all, and
    /// "a poll noticed it" and "an operator pinned it" are different facts about the same transition.
    /// </remarks>
    [Required, MaxLength(32)]
    public string Cause { get; set; } = string.Empty;

    /// <summary>The operator's object id, when a person caused it. Null for every machine path.</summary>
    [MaxLength(64)]
    public string? ActorOid { get; set; }

    /// <summary>The kind of actor (see <c>ActorKinds</c>).</summary>
    [MaxLength(32)]
    public string? ActorKind { get; set; }

    /// <summary>The actor's display name, captured because it cannot be resolved afterwards.</summary>
    [MaxLength(200)]
    public string? ActorName { get; set; }

    /// <summary>When it changed, as this service observed it. Always UTC.</summary>
    public DateTime At { get; set; }
}
