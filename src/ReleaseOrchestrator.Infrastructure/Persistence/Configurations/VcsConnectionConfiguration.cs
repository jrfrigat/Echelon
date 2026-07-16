using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ReleaseOrchestrator.Core.Entities;

namespace ReleaseOrchestrator.Infrastructure.Persistence.Configurations;

/// <summary>EF mapping for <see cref="VcsConnection"/>.</summary>
public class VcsConnectionConfiguration : IEntityTypeConfiguration<VcsConnection>
{
    /// <inheritdoc/>
    public void Configure(EntityTypeBuilder<VcsConnection> b)
    {
        ArgumentNullException.ThrowIfNull(b);

        b.HasKey(e => e.Id);
        b.Property(e => e.Name).HasMaxLength(200).IsRequired();
        b.HasIndex(e => e.Name).IsUnique().HasDatabaseName("UQ_VcsConnection_Name");
        b.Property(e => e.ApiUrl).HasMaxLength(500).IsRequired();

        // Was an int-backed enum. Now the adapter's key, stored as text: a provider is added by
        // registering an adapter, not by widening a domain enum and migrating this column.
        b.Property(e => e.ProviderType).HasMaxLength(100).IsRequired();

        b.Property(e => e.ReadyForDeployLabel).HasMaxLength(200);
    }
}
