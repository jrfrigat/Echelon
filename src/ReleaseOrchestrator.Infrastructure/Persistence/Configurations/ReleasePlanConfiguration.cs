using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ReleaseOrchestrator.Core.Entities;

namespace ReleaseOrchestrator.Infrastructure.Persistence.Configurations;

public class ReleasePlanConfiguration : IEntityTypeConfiguration<ReleasePlan>
{
    public void Configure(EntityTypeBuilder<ReleasePlan> b)
    {
        b.HasKey(e => e.Id);
        b.Property(e => e.Name).HasMaxLength(300).IsRequired();
        b.Property(e => e.Version).HasMaxLength(50).IsRequired();
        b.Property(e => e.YamlHash).HasMaxLength(64);
        // Filtered unique index: at most one active plan, enforced by the database
        // rather than by convention. Concurrent recalculations previously left two.
        b.HasIndex(e => e.IsActive)
            .IsUnique()
            .HasFilter("[IsActive] = 1")
            .HasDatabaseName("IX_ReleasePlan_IsActive");
        b.HasIndex(e => e.CreatedAt).HasDatabaseName("IX_ReleasePlan_CreatedAt");
    }
}

public class ReleaseStageConfiguration : IEntityTypeConfiguration<ReleaseStage>
{
    public void Configure(EntityTypeBuilder<ReleaseStage> b)
    {
        b.HasKey(e => e.Id);
        b.Property(e => e.Name).HasMaxLength(300);
        b.HasOne(e => e.Plan).WithMany(p => p.Stages).HasForeignKey(e => e.PlanId).OnDelete(DeleteBehavior.Cascade);
    }
}

public class StageItemConfiguration : IEntityTypeConfiguration<StageItem>
{
    public void Configure(EntityTypeBuilder<StageItem> b)
    {
        b.HasKey(e => e.Id);
        b.HasIndex(e => e.MergeRequestId).HasDatabaseName("IX_StageItem_MergeRequestId");
        b.HasOne(e => e.Stage).WithMany(s => s.Items).HasForeignKey(e => e.StageId).OnDelete(DeleteBehavior.Cascade);
        b.HasOne(e => e.MergeRequest).WithMany(mr => mr.StageItems).HasForeignKey(e => e.MergeRequestId).OnDelete(DeleteBehavior.Restrict);
    }
}
