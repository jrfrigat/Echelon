using Microsoft.EntityFrameworkCore;
using ReleaseOrchestrator.Infrastructure.Archive.Entities;

namespace ReleaseOrchestrator.Infrastructure.Archive;

public class ArchiveDbContext(DbContextOptions<ArchiveDbContext> options) : DbContext(options)
{
    public DbSet<ArchivedTask> ArchivedTasks => Set<ArchivedTask>();
    public DbSet<ArchivedMergeRequest> ArchivedMergeRequests => Set<ArchivedMergeRequest>();

    /// <summary>
    /// HTTP request telemetry. Here rather than in the operational database so audit write volume
    /// never competes with the release engine, and so one credential cannot both write the audit and
    /// erase it.
    /// </summary>
    public DbSet<RequestAuditEntry> RequestAuditEntries => Set<RequestAuditEntry>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<ArchivedTask>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.ExternalId).HasMaxLength(200).IsRequired();
            e.Property(x => x.Title).HasMaxLength(500);
            e.Property(x => x.Status).HasMaxLength(100);
            e.Property(x => x.FirstSeenSource).HasMaxLength(200);
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

        b.Entity<RequestAuditEntry>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Host).HasMaxLength(40).IsRequired();
            e.Property(x => x.Instance).HasMaxLength(100).IsRequired();
            e.Property(x => x.Method).HasMaxLength(10).IsRequired();
            e.Property(x => x.RoutePattern).HasMaxLength(200).IsRequired();
            e.Property(x => x.Path).HasMaxLength(300).IsRequired();
            e.Property(x => x.Kind).HasMaxLength(20).IsRequired();
            e.Property(x => x.UserId).HasMaxLength(64);
            e.Property(x => x.UserName).HasMaxLength(256);
            e.Property(x => x.PeerIp).HasMaxLength(64);
            e.Property(x => x.ForwardedIp).HasMaxLength(64);
            e.Property(x => x.Permission).HasMaxLength(200);
            e.Property(x => x.CorrelationId).HasMaxLength(64).IsRequired();
            e.Property(x => x.ExceptionType).HasMaxLength(200);

            // The newest-first page, and the pruner's cutoff scan.
            e.HasIndex(x => x.StartedAt).HasDatabaseName("IX_RequestAuditEntries_StartedAt");
            // The default view (failures and mutations) and the long-retention tier, which are the
            // same predicate -- deliberately, so they cannot disagree about what "notable" means.
            e.HasIndex(x => new { x.IsNotable, x.StartedAt }).HasDatabaseName("IX_RequestAuditEntries_Notable_StartedAt");
            // "What did this person do."
            e.HasIndex(x => new { x.UserId, x.StartedAt }).HasDatabaseName("IX_RequestAuditEntries_UserId_StartedAt");

            // No index on RoutePattern: the summary always groups inside a StartedAt range, so it
            // scans a bounded slice. A fourth B-tree write per insert is unjustified until measured.
        });
    }
}
