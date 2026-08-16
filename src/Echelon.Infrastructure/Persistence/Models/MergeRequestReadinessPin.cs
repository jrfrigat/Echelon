using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Echelon.Infrastructure.Persistence.Models;

/// <summary>
/// A person's decision that a merge request is - or is not - ready for one environment, overriding
/// whatever its labels would say.
/// </summary>
/// <remarks>
/// <para>
/// The escape hatch a label gate needs. Labels cannot always be acquired: a merged merge request has
/// left the poller's open-request listing, so a <c>ready-for-prod</c> approval that lands after the
/// merge may never be observable from labels, and the first production rule would then wedge every
/// task holding such a merge request. A pin lets a person say "ready" anyway. Because it can also say
/// "not ready", it doubles as a hold - keeping a merge request out even though its labels would admit
/// it.
/// </para>
/// <para>
/// Unlike the label journals, this is live state a decision is read from, not history, so it has real
/// foreign keys: to the merge request with <see cref="DeleteBehavior.Cascade"/>, since a pin is
/// meaningless once the merge request is gone, and to the environment with
/// <see cref="DeleteBehavior.Restrict"/>, which keeps this table to one cascade path (SQL Server's
/// requirement) and makes deleting a still-referenced environment a deliberate act.
/// </para>
/// </remarks>
[Index(nameof(MergeRequestId), nameof(EnvironmentId), IsUnique = true, Name = "IX_MergeRequestReadinessPin_Mr_Environment")]
public class MergeRequestReadinessPin
{
    /// <summary>Primary key.</summary>
    [Key]
    public Guid Id { get; set; }

    /// <summary>The merge request this decides for.</summary>
    public Guid MergeRequestId { get; set; }

    /// <summary>The environment this decides for.</summary>
    public Guid EnvironmentId { get; set; }

    /// <summary>The decision: true admits the merge request, false holds it out.</summary>
    public bool IsReady { get; set; }

    /// <summary>Why the person pinned it, in their words. Optional; shown in the readiness history.</summary>
    [MaxLength(500)]
    public string? Reason { get; set; }

    /// <summary>The pinning operator's object id.</summary>
    [MaxLength(64)]
    public string? ActorOid { get; set; }

    /// <summary>The kind of actor (see <c>ActorKinds</c>).</summary>
    [MaxLength(32)]
    public string? ActorKind { get; set; }

    /// <summary>The actor's display name, captured because it cannot be resolved afterwards.</summary>
    [MaxLength(200)]
    public string? ActorName { get; set; }

    /// <summary>When it was pinned or last changed. Always UTC.</summary>
    public DateTime At { get; set; }

    /// <summary>Concurrency token: two operators can pin the same pair at once.</summary>
    [Timestamp]
    public byte[]? RowVersion { get; set; }

    /// <summary>The merge request. Cascade: the pin dies with it.</summary>
    [ForeignKey(nameof(MergeRequestId))]
    [DeleteBehavior(DeleteBehavior.Cascade)]
    public MergeRequest MergeRequest { get; set; } = null!;

    /// <summary>The environment. Restrict: keeps this table to one cascade path and forces a deliberate delete.</summary>
    [ForeignKey(nameof(EnvironmentId))]
    [InverseProperty(nameof(DeploymentEnvironment.ReadinessPins))]
    [DeleteBehavior(DeleteBehavior.Restrict)]
    public DeploymentEnvironment Environment { get; set; } = null!;
}
