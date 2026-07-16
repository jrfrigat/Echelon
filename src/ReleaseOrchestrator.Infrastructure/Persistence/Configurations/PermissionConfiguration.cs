using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ReleaseOrchestrator.Core.Entities;

namespace ReleaseOrchestrator.Infrastructure.Persistence.Configurations;

public class PermissionClaimConfiguration : IEntityTypeConfiguration<PermissionClaim>
{
    public void Configure(EntityTypeBuilder<PermissionClaim> b)
    {
        b.HasKey(e => e.Id);
        b.Property(e => e.Name).HasMaxLength(200).IsRequired();
        b.HasIndex(e => e.Name).IsUnique();
    }
}

public class GroupPermissionMappingConfiguration : IEntityTypeConfiguration<GroupPermissionMapping>
{
    public void Configure(EntityTypeBuilder<GroupPermissionMapping> b)
    {
        b.HasKey(e => e.Id);
        b.Property(e => e.AdGroupSid).HasMaxLength(200).IsRequired();
        b.HasOne(e => e.PermissionClaim).WithMany(c => c.GroupMappings).HasForeignKey(e => e.PermissionClaimId).OnDelete(DeleteBehavior.Cascade);
    }
}

public class UserPermissionOverrideConfiguration : IEntityTypeConfiguration<UserPermissionOverride>
{
    public void Configure(EntityTypeBuilder<UserPermissionOverride> b)
    {
        b.HasKey(e => e.Id);
        b.Property(e => e.UserId).HasMaxLength(450).IsRequired();
        b.HasOne(e => e.PermissionClaim).WithMany(c => c.UserOverrides).HasForeignKey(e => e.PermissionClaimId).OnDelete(DeleteBehavior.Cascade);
    }
}
