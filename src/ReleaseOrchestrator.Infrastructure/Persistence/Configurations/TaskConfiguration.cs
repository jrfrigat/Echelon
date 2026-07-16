using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ReleaseOrchestrator.Core.Entities;

namespace ReleaseOrchestrator.Infrastructure.Persistence.Configurations;

public class TaskItemConfiguration : IEntityTypeConfiguration<TaskItem>
{
    public void Configure(EntityTypeBuilder<TaskItem> b)
    {
        b.HasKey(e => e.Id);
        b.Property(e => e.ExternalId).HasMaxLength(200).IsRequired();
        b.Property(e => e.Title).HasMaxLength(500).IsRequired();
        b.Property(e => e.Status).HasMaxLength(100).IsRequired();
        b.HasIndex(e => e.ExternalId).HasDatabaseName("IX_TaskItem_ExternalId");
        b.HasIndex(e => new { e.TrackerConnectionId, e.ExternalId }).HasDatabaseName("IX_TaskItem_TrackerConnectionId_ExternalId");
        b.HasOne(e => e.TrackerConnection).WithMany(c => c.Tasks).HasForeignKey(e => e.TrackerConnectionId).OnDelete(DeleteBehavior.Restrict);
    }
}

public class TaskDependencyConfiguration : IEntityTypeConfiguration<TaskDependency>
{
    public void Configure(EntityTypeBuilder<TaskDependency> b)
    {
        b.HasKey(e => e.Id);
        b.HasOne(e => e.DependentTask).WithMany(t => t.Dependents).HasForeignKey(e => e.DependentTaskId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(e => e.DependsOnTask).WithMany(t => t.Dependencies).HasForeignKey(e => e.DependsOnTaskId).OnDelete(DeleteBehavior.Restrict);
    }
}
