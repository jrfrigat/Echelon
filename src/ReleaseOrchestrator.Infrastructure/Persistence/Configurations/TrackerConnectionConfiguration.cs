using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ReleaseOrchestrator.Core.Entities;

namespace ReleaseOrchestrator.Infrastructure.Persistence.Configurations;

/// <summary>EF mapping for <see cref="TrackerConnection"/>.</summary>
public class TrackerConnectionConfiguration : IEntityTypeConfiguration<TrackerConnection>
{
    /// <inheritdoc/>
    public void Configure(EntityTypeBuilder<TrackerConnection> b)
    {
        ArgumentNullException.ThrowIfNull(b);

        b.HasKey(e => e.Id);
        b.Property(e => e.Name).HasMaxLength(200).IsRequired();
        b.Property(e => e.ApiUrl).HasMaxLength(500).IsRequired();

        // See VcsConnectionConfiguration: the enum became the adapter's key.
        b.Property(e => e.ProviderType).HasMaxLength(100).IsRequired();

        // Replaced the OrgId column. Adapter-owned JSON, so a provider's own settings do not each
        // add a named column to a shared table. Read only by the adapter that wrote it.
        b.Property(e => e.ProviderSettingsJson).HasMaxLength(4000);
    }
}
