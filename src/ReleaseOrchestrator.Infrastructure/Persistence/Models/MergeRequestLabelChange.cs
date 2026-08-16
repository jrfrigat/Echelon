using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;

namespace ReleaseOrchestrator.Infrastructure.Persistence.Models;

/// <summary>
/// One recorded change to a merge request's label set: what it was, what it became, what caused it,
/// and who - when anybody did.
/// </summary>
/// <remarks>
/// <para>
/// The sibling of <see cref="MergeRequestStatusChange"/>, and for the same reason: labels drive the
/// per-environment readiness gate, <see cref="MergeRequest.Labels"/> is overwritten in place on each
/// observation, and so the moment a merge request became ready for an environment - gained
/// <c>ready-for-prod</c> - would otherwise leave no trace. This is where "when did it become
/// deployable to prod" is answered.
/// </para>
/// <para>
/// Deliberately has NO foreign key and NO navigation on <see cref="MergeRequest"/>, identical to the
/// status journal: an FK would force cascade (archiving destroys the history) or restrict (the
/// archive's <c>ExecuteDeleteAsync</c> FK-violates and wedges the batch). Independence in both
/// directions is the point, and the price is the denormalised <see cref="MergeRequestExternalId"/>,
/// which is all that names the entry after the merge request row is gone.
/// </para>
/// </remarks>
[Index(nameof(TaskId), nameof(At), Name = "IX_MergeRequestLabelChange_TaskId_At")]
[Index(nameof(MergeRequestId), nameof(At), Name = "IX_MergeRequestLabelChange_MergeRequestId_At")]
public class MergeRequestLabelChange
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

    /// <summary>
    /// The label set before the change, canonical (comma-joined). Empty string when there were no
    /// labels before - including a merge request seen for the first time, whose stored set starts
    /// empty. Nullable only to match the status journal's shape; the writer never stores null.
    /// </summary>
    [MaxLength(2000)]
    public string? FromLabels { get; set; }

    /// <summary>The label set after the change, canonical. Empty string when it now has no labels.</summary>
    [Required, MaxLength(2000)]
    public string ToLabels { get; set; } = string.Empty;

    /// <summary>
    /// What drove it: <c>webhook</c>, <c>poll</c> or <c>label</c> - the same causes as the status
    /// journal, minus <c>manual-pin</c>, since labels are never set by hand.
    /// </summary>
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
