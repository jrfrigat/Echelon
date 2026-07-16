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
        b.HasIndex(e => new { e.RepositoryId, e.Status }).HasDatabaseName("IX_MergeRequest_RepositoryId_Status");
        b.HasIndex(e => e.TaskId).HasDatabaseName("IX_MergeRequest_TaskId");
        b.HasOne(e => e.Repository).WithMany(r => r.MergeRequests).HasForeignKey(e => e.RepositoryId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(e => e.Task).WithMany(t => t.MergeRequests).HasForeignKey(e => e.TaskId).OnDelete(DeleteBehavior.SetNull);
    }
}
