using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ReleaseOrchestrator.Infrastructure.Persistence.Models;

namespace ReleaseOrchestrator.Infrastructure.Persistence.Configurations;

/// <summary>
/// The one thing about <see cref="ReleasePlan"/> that no attribute can say.
/// </summary>
public class ReleasePlanConfiguration : IEntityTypeConfiguration<ReleasePlan>
{
    /// <inheritdoc/>
    public void Configure(EntityTypeBuilder<ReleasePlan> b)
    {
        ArgumentNullException.ThrowIfNull(b);

        // At most one active plan, enforced by the database rather than by convention: the planner
        // deactivates the previous plan before inserting, but two concurrent recalculations both
        // deactivate before either inserts, and that left two active. A plain unique index on a
        // bool would also permit only one *inactive* plan ever, so the filter is what makes it
        // work — and [Index] has no filter, so this cannot move onto the model.
        b.HasIndex(e => e.IsActive)
            .IsUnique()
            .HasFilter("[IsActive] = 1")
            .HasDatabaseName("IX_ReleasePlan_IsActive");
    }
}
