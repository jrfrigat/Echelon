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
        b.Property(e => e.RowVersion).IsRowVersion();
        b.HasIndex(e => e.ExternalId).HasDatabaseName("IX_TaskItem_ExternalId");
        // Natural key. Unique because consumers upsert via check-then-insert, which
        // races with itself under at-least-once delivery and multiple replicas.
        b.HasIndex(e => new { e.TrackerConnectionId, e.ExternalId }).IsUnique().HasDatabaseName("IX_TaskItem_TrackerConnectionId_ExternalId");
        b.HasIndex(e => e.ClosedAt).HasDatabaseName("IX_TaskItem_ClosedAt");
        b.HasOne(e => e.TrackerConnection).WithMany(c => c.Tasks).HasForeignKey(e => e.TrackerConnectionId).OnDelete(DeleteBehavior.Restrict);
    }
}

public class TaskDependencyConfiguration : IEntityTypeConfiguration<TaskDependency>
{
    public void Configure(EntityTypeBuilder<TaskDependency> b)
    {
        b.HasKey(e => e.Id);
        // A row (DependentTaskId = B, DependsOnTaskId = A) reads "B depends on A".
        // So B.Dependencies are the rows naming B as the dependent, and A.Dependents
        // are the rows naming A as the prerequisite. Binding these the other way round
        // makes task.Dependencies yield rows whose DependsOnTaskId is task's own id,
        // which collapses every dependency edge into a self-loop.
        b.HasOne(e => e.DependentTask).WithMany(t => t.Dependencies).HasForeignKey(e => e.DependentTaskId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(e => e.DependsOnTask).WithMany(t => t.Dependents).HasForeignKey(e => e.DependsOnTaskId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(e => new { e.DependentTaskId, e.DependsOnTaskId }).IsUnique().HasDatabaseName("IX_TaskDependency_Dependent_DependsOn");
    }
}
