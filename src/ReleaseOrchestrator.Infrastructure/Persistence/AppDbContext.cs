using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using ReleaseOrchestrator.Infrastructure.Persistence.Models;
using ReleaseOrchestrator.Infrastructure.Persistence.Configurations;

namespace ReleaseOrchestrator.Infrastructure.Persistence;

public class AppDbContext(DbContextOptions<AppDbContext> options)
    : IdentityDbContext(options), IDataProtectionKeyContext
{
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

    public DbSet<VcsConnection> VcsConnections => Set<VcsConnection>();
    public DbSet<TrackerConnection> TrackerConnections => Set<TrackerConnection>();
    public DbSet<Repository> Repositories => Set<Repository>();
    public DbSet<DeploymentEnvironment> DeploymentEnvironments => Set<DeploymentEnvironment>();
    public DbSet<ReadinessRule> ReadinessRules => Set<ReadinessRule>();
    public DbSet<RepositoryDeployTarget> RepositoryDeployTargets => Set<RepositoryDeployTarget>();
    public DbSet<MergeRequestReadinessPin> MergeRequestReadinessPins => Set<MergeRequestReadinessPin>();
    public DbSet<RepositoryDependency> RepositoryDependencies => Set<RepositoryDependency>();
    public DbSet<TaskItem> Tasks => Set<TaskItem>();
    public DbSet<TaskDependency> TaskDependencies => Set<TaskDependency>();
    public DbSet<MergeRequest> MergeRequests => Set<MergeRequest>();
    public DbSet<RepositoryBranch> RepositoryBranches => Set<RepositoryBranch>();
    public DbSet<MergeRequestStatusChange> MergeRequestStatusChanges => Set<MergeRequestStatusChange>();
    public DbSet<MergeRequestLabelChange> MergeRequestLabelChanges => Set<MergeRequestLabelChange>();
    public DbSet<RolloutPlan> RolloutPlans => Set<RolloutPlan>();
    public DbSet<PlanTaskNode> PlanTaskNodes => Set<PlanTaskNode>();
    public DbSet<PlanItem> PlanItems => Set<PlanItem>();
    public DbSet<PlanOverride> PlanOverrides => Set<PlanOverride>();
    public DbSet<TaskPrerequisiteOrder> TaskPrerequisiteOrders => Set<TaskPrerequisiteOrder>();
    public DbSet<PlanningSettings> PlanningSettings => Set<PlanningSettings>();
    public DbSet<MrDeploymentState> MrDeploymentStates => Set<MrDeploymentState>();
    public DbSet<Rollout> Rollouts => Set<Rollout>();
    public DbSet<RolloutStep> RolloutSteps => Set<RolloutStep>();
    public DbSet<MrDeployClaim> MrDeployClaims => Set<MrDeployClaim>();
    public DbSet<RolloutStepAttempt> RolloutStepAttempts => Set<RolloutStepAttempt>();
    public DbSet<RolloutEvent> RolloutEvents => Set<RolloutEvent>();
    public DbSet<ProcessedEvent> ProcessedEvents => Set<ProcessedEvent>();
    public DbSet<ActionBinding> ActionBindings => Set<ActionBinding>();
    public DbSet<PermissionClaim> PermissionClaims => Set<PermissionClaim>();
    public DbSet<GroupPermissionMapping> GroupPermissionMappings => Set<GroupPermissionMapping>();
    public DbSet<UserPermissionOverride> UserPermissionOverrides => Set<UserPermissionOverride>();

    /// <inheritdoc/>
    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);

        // Last, and deliberately after the shared configuration: it overrides the two mappings that
        // SQL Server and PostgreSQL cannot share. See ProviderSpecificMapping - both were found by
        // building the model, not by reading, and one of them fails silently.
        builder.ApplyProviderSpecifics(Database.ProviderName);
    }
}
