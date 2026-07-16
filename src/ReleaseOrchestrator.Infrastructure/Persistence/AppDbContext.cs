using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using ReleaseOrchestrator.Core.Entities;
using ReleaseOrchestrator.Infrastructure.Persistence.Configurations;

namespace ReleaseOrchestrator.Infrastructure.Persistence;

public class AppDbContext(DbContextOptions<AppDbContext> options)
    : IdentityDbContext(options), IDataProtectionKeyContext
{
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

    public DbSet<VcsConnection> VcsConnections => Set<VcsConnection>();
    public DbSet<TrackerConnection> TrackerConnections => Set<TrackerConnection>();
    public DbSet<Repository> Repositories => Set<Repository>();
    public DbSet<Stack> Stacks => Set<Stack>();
    public DbSet<RepositoryStack> RepositoryStacks => Set<RepositoryStack>();
    public DbSet<StackDependency> StackDependencies => Set<StackDependency>();
    public DbSet<TaskItem> Tasks => Set<TaskItem>();
    public DbSet<TaskDependency> TaskDependencies => Set<TaskDependency>();
    public DbSet<MergeRequest> MergeRequests => Set<MergeRequest>();
    public DbSet<ReleasePlan> ReleasePlans => Set<ReleasePlan>();
    public DbSet<ReleaseStage> ReleaseStages => Set<ReleaseStage>();
    public DbSet<StageItem> StageItems => Set<StageItem>();
    public DbSet<PermissionClaim> PermissionClaims => Set<PermissionClaim>();
    public DbSet<GroupPermissionMapping> GroupPermissionMappings => Set<GroupPermissionMapping>();
    public DbSet<UserPermissionOverride> UserPermissionOverrides => Set<UserPermissionOverride>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);
    }
}
