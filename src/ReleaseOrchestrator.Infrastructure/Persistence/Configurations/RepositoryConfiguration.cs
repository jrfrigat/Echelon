using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ReleaseOrchestrator.Core.Entities;

namespace ReleaseOrchestrator.Infrastructure.Persistence.Configurations;

public class RepositoryConfiguration : IEntityTypeConfiguration<Repository>
{
    public void Configure(EntityTypeBuilder<Repository> b)
    {
        b.HasKey(e => e.Id);
        b.Property(e => e.Name).HasMaxLength(300).IsRequired();
        b.Property(e => e.ExternalId).HasMaxLength(500).IsRequired();
        b.HasOne(e => e.Connection)
            .WithMany(c => c.Repositories)
            .HasForeignKey(e => e.ConnectionId)
            .OnDelete(DeleteBehavior.Restrict);
        b.HasOne(e => e.TrackerConnection)
            .WithMany(c => c.Repositories)
            .HasForeignKey(e => e.TrackerConnectionId)
            .OnDelete(DeleteBehavior.Restrict);
        // Natural key: VcsService and the YAML import both resolve a repository by this pair.
        b.HasIndex(e => new { e.ConnectionId, e.ExternalId }).IsUnique().HasDatabaseName("IX_Repository_ConnectionId_ExternalId");
    }
}
