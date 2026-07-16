using Microsoft.EntityFrameworkCore;
using ReleaseOrchestrator.Infrastructure.Archive.Entities;

namespace ReleaseOrchestrator.Infrastructure.Archive;

public class ArchiveDbContext(DbContextOptions<ArchiveDbContext> options) : DbContext(options)
{
    public DbSet<ArchivedTask> ArchivedTasks => Set<ArchivedTask>();
    public DbSet<ArchivedMergeRequest> ArchivedMergeRequests => Set<ArchivedMergeRequest>();
    public DbSet<ArchivedReleasePlan> ArchivedReleasePlans => Set<ArchivedReleasePlan>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<ArchivedTask>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.ExternalId).HasMaxLength(200).IsRequired();
            e.Property(x => x.Title).HasMaxLength(500);
            e.Property(x => x.Status).HasMaxLength(100);
            e.HasIndex(x => x.ExternalId);
            e.HasIndex(x => x.ClosedAt);
        });

        b.Entity<ArchivedMergeRequest>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.ExternalId).HasMaxLength(200).IsRequired();
            e.Property(x => x.RepositoryName).HasMaxLength(300);
            e.Property(x => x.SourceBranch).HasMaxLength(500);
            e.Property(x => x.TargetBranch).HasMaxLength(500);
            e.Property(x => x.Status).HasMaxLength(100);
            e.Property(x => x.TaskExternalId).HasMaxLength(200);
            e.HasIndex(x => x.TaskExternalId);
            e.HasIndex(x => x.MergedAt);
            e.HasIndex(x => x.ClosedAt);
        });

        b.Entity<ArchivedReleasePlan>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(300);
            e.Property(x => x.Version).HasMaxLength(50);
            e.HasIndex(x => x.CreatedAt);
        });
    }
}
