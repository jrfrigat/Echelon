using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ReleaseOrchestrator.Core.Entities;

namespace ReleaseOrchestrator.Infrastructure.Persistence.Configurations;

public class StackConfiguration : IEntityTypeConfiguration<Stack>
{
    public void Configure(EntityTypeBuilder<Stack> b)
    {
        b.HasKey(e => e.Id);
        b.Property(e => e.Name).HasMaxLength(200).IsRequired();
        b.HasIndex(e => e.Name).IsUnique();
    }
}

public class RepositoryStackConfiguration : IEntityTypeConfiguration<RepositoryStack>
{
    public void Configure(EntityTypeBuilder<RepositoryStack> b)
    {
        b.HasKey(e => new { e.RepositoryId, e.StackId });
        b.HasOne(e => e.Repository).WithMany(r => r.RepositoryStacks).HasForeignKey(e => e.RepositoryId);
        b.HasOne(e => e.Stack).WithMany(s => s.RepositoryStacks).HasForeignKey(e => e.StackId);
    }
}

public class StackDependencyConfiguration : IEntityTypeConfiguration<StackDependency>
{
    public void Configure(EntityTypeBuilder<StackDependency> b)
    {
        b.HasKey(e => e.Id);
        b.HasOne(e => e.FromStack).WithMany(s => s.DependentOn).HasForeignKey(e => e.FromStackId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne(e => e.ToStack).WithMany(s => s.RequiredBy).HasForeignKey(e => e.ToStackId).OnDelete(DeleteBehavior.Restrict);
    }
}
