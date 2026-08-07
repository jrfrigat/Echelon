using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;
using ReleaseOrchestrator.Core.Enums;

namespace ReleaseOrchestrator.Infrastructure.Persistence.Models;

/// <summary>
/// The orchestrator's deployment truth for one merge request in one environment.
/// </summary>
/// <remarks>
/// Keyed per (merge request, environment): the same merge request can be Deployed in staging and
/// NotStarted in prod. Separate from <see cref="MergeRequest.Status"/> (the ingestion truth), so the
/// two cannot overwrite each other.
/// </remarks>
[PrimaryKey(nameof(MergeRequestId), nameof(EnvironmentId))]
public class MrDeploymentState
{
    /// <summary>The merge request.</summary>
    public Guid MergeRequestId { get; set; }

    /// <summary>The environment.</summary>
    public Guid EnvironmentId { get; set; }

    /// <summary>What the orchestrator has done with this merge request in this environment.</summary>
    public DeploymentState State { get; set; }

    /// <summary>When the state last changed.</summary>
    public DateTime UpdatedAt { get; set; }

    /// <summary>Optimistic-concurrency token. See <c>ProviderSpecificMapping</c> for the Postgres mapping.</summary>
    [Timestamp]
    public byte[]? RowVersion { get; set; }

    /// <summary>The merge request. Restrict: deployment history is not orphaned by archiving.</summary>
    [ForeignKey(nameof(MergeRequestId))]
    [DeleteBehavior(DeleteBehavior.Restrict)]
    public MergeRequest MergeRequest { get; set; } = null!;

    /// <summary>The environment. Restrict.</summary>
    [ForeignKey(nameof(EnvironmentId))]
    [DeleteBehavior(DeleteBehavior.Restrict)]
    public DeploymentEnvironment Environment { get; set; } = null!;
}
