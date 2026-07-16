using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ReleaseOrchestrator.Core.Entities;

namespace ReleaseOrchestrator.Infrastructure.Persistence.Configurations;

public class TrackerConnectionConfiguration : IEntityTypeConfiguration<TrackerConnection>
{
    public void Configure(EntityTypeBuilder<TrackerConnection> b)
    {
        b.HasKey(e => e.Id);
        b.Property(e => e.Name).HasMaxLength(200).IsRequired();
        b.Property(e => e.ApiUrl).HasMaxLength(500).IsRequired();
        b.Property(e => e.OrgId).HasMaxLength(200);
    }
}
