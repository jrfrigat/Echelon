using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ReleaseOrchestrator.Core.Entities;
using ReleaseOrchestrator.Core.Enums;

namespace ReleaseOrchestrator.Infrastructure.Persistence.Configurations;

public class MergeRequestConfiguration : IEntityTypeConfiguration<MergeRequest>
{
    public void Configure(EntityTypeBuilder<MergeRequest> b)
    {
        b.HasKey(e => e.Id);
        b.Property(e => e.ExternalId).HasMaxLength(200).IsRequired();
        b.Property(e => e.SourceBranch).HasMaxLength(500).IsRequired();
        b.Property(e => e.TargetBranch).HasMaxLength(500).IsRequired();
        b.Property(e => e.Status)
            .HasConversion(v => v.ToString(), v => Enum.Parse<MergeRequestStatus>(v))
            .HasMaxLength(50);
        b.Property(e => e.RowVersion).IsRowVersion();
        b.Property(e => e.TaskExternalId).HasMaxLength(200);
        // Linking an MR to a task that arrives later is a lookup on this.
        b.HasIndex(e => e.TaskExternalId).HasDatabaseName("IX_MergeRequest_TaskExternalId");
        b.HasIndex(e => new { e.RepositoryId, e.Status }).HasDatabaseName("IX_MergeRequest_RepositoryId_Status");
        b.HasIndex(e => e.TaskId).HasDatabaseName("IX_MergeRequest_TaskId");
        // Natural key. Unique because consumers upsert via check-then-insert, which
        // races with itself under at-least-once delivery and multiple replicas.
        b.HasIndex(e => new { e.RepositoryId, e.ExternalId }).IsUnique().HasDatabaseName("IX_MergeRequest_RepositoryId_ExternalId");
        // The planner filters on Status alone; RepositoryId leading means that index
        // cannot seek for it.
        b.HasIndex(e => e.Status).HasDatabaseName("IX_MergeRequest_Status");
        b.HasIndex(e => e.MergedAt).HasDatabaseName("IX_MergeRequest_MergedAt");
        b.HasIndex(e => e.ClosedAt).HasDatabaseName("IX_MergeRequest_ClosedAt");
        b.HasOne(e => e.Repository).WithMany(r => r.MergeRequests).HasForeignKey(e => e.RepositoryId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(e => e.Task).WithMany(t => t.MergeRequests).HasForeignKey(e => e.TaskId).OnDelete(DeleteBehavior.SetNull);
    }
}
