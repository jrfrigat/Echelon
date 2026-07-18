using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ReleaseOrchestrator.Infrastructure.Persistence.Models;

namespace ReleaseOrchestrator.Infrastructure.Persistence.Configurations;

/// <summary>
/// The one thing about <see cref="RolloutPlan"/> that no attribute can say: at most one active plan
/// per target task.
/// </summary>
public class RolloutPlanConfiguration : IEntityTypeConfiguration<RolloutPlan>
{
    /// <inheritdoc/>
    public void Configure(EntityTypeBuilder<RolloutPlan> b)
    {
        ArgumentNullException.ThrowIfNull(b);

        // One active plan PER task, enforced by the database. Same reasoning as ReleasePlan's global
        // index (two concurrent recalculations both deactivate before either inserts), scoped to the
        // task by filtering on IsActive: among active rows, TargetTaskId is unique. [Index] has no
        // filter, so this cannot live on the model. The SQL Server dialect is written here; the
        // PostgreSQL rewrite is in ProviderSpecificMapping.
        b.HasIndex(e => e.TargetTaskId)
            .IsUnique()
            .HasFilter("[IsActive] = 1")
            .HasDatabaseName("IX_RolloutPlan_TargetTaskId_Active");
    }
}
