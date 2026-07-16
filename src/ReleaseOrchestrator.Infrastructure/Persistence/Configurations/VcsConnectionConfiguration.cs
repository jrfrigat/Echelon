using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ReleaseOrchestrator.Core.Entities;

namespace ReleaseOrchestrator.Infrastructure.Persistence.Configurations;

public class VcsConnectionConfiguration : IEntityTypeConfiguration<VcsConnection>
{
    public void Configure(EntityTypeBuilder<VcsConnection> b)
    {
        b.HasKey(e => e.Id);
        b.Property(e => e.Name).HasMaxLength(200).IsRequired();
        b.HasIndex(e => e.Name).IsUnique().HasDatabaseName("UQ_VcsConnection_Name");
        b.Property(e => e.ApiUrl).HasMaxLength(500).IsRequired();
        b.Property(e => e.VcsType).IsRequired();
    }
}
