using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Echelon.Infrastructure.Persistence.Models;

namespace Echelon.Infrastructure.Persistence.Configurations;

/// <summary>
/// The one thing about <see cref="Rollout"/> that no attribute can say: at most one live rollout per
/// (target task, environment).
/// </summary>
public class RolloutConfiguration : IEntityTypeConfiguration<Rollout>
{
    /// <inheritdoc/>
    public void Configure(EntityTypeBuilder<Rollout> b)
    {
        ArgumentNullException.ThrowIfNull(b);

        // One LIVE rollout per (task, environment), enforced by the database, the same way one active
        // plan per task is (see RolloutPlanConfiguration). The application checks this before
        // inserting, but a plain read under READ COMMITTED cannot hold the invariant across the
        // check-to-insert window: two launches straddling a plan recalculation compute different
        // idempotency keys -- the key embeds the plan id, which the recalculation rotates -- so
        // neither the key's own unique index nor the read-based check stops both inserting a live
        // run. Among non-terminal rows, this makes (TargetTaskId, EnvironmentId) unique, so the second
        // insert fails and LaunchAsync's catch turns it into a clean "already running".
        //
        // The filter is on the integer Status column: terminal is 5 Cancelled, 6 Succeeded, 7 Failed
        // (RolloutStatus). Written as three <> joined by AND, rather than the more natural
        // "NOT IN (5,6,7)", because a SQL Server filtered-index predicate rejects NOT IN as a syntax
        // error (verified on the database, not from docs). The <> form is equivalent and preferred
        // over "< 5" so a status added later is presumed live -- and so blocking -- until this filter
        // is deliberately taught otherwise, which is the safe direction. [Index] has no filter, so
        // this cannot live on the model; the PostgreSQL rewrite of the filter is in
        // ProviderSpecificMapping.
        b.HasIndex(e => new { e.TargetTaskId, e.EnvironmentId })
            .IsUnique()
            .HasFilter("[Status] <> 5 AND [Status] <> 6 AND [Status] <> 7")
            .HasDatabaseName("IX_Rollout_TargetTask_Environment_Live");
    }
}
