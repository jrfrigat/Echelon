using Microsoft.EntityFrameworkCore;
using ReleaseOrchestrator.Infrastructure.Persistence.Models;

namespace ReleaseOrchestrator.Infrastructure.Persistence;

/// <summary>
/// The three places where SQL Server and PostgreSQL do not describe the same thing the same way.
/// </summary>
/// <remarks>
/// Everything else in this model is provider-neutral and stays on the entities. These three are here
/// because they cannot be: a string of SQL, a column only one database has, and a collation whose
/// name is dialect-specific. The first two were found by asking EF to build the PostgreSQL model
/// rather than by reading about it; the third by inserting two rows into a real SQL Server. All
/// three are worth reading before touching.
/// </remarks>
public static class ProviderSpecificMapping
{
    /// <summary>EF's provider name for Npgsql.</summary>
    public const string NpgsqlProvider = "Npgsql.EntityFrameworkCore.PostgreSQL";

    /// <summary>EF's provider name for SQL Server.</summary>
    public const string SqlServerProvider = "Microsoft.EntityFrameworkCore.SqlServer";

    /// <summary>True when the model is being built for PostgreSQL.</summary>
    public static bool IsPostgreSql(string? providerName) =>
        string.Equals(providerName, NpgsqlProvider, StringComparison.Ordinal);

    /// <summary>True when the model is being built for SQL Server.</summary>
    public static bool IsSqlServer(string? providerName) =>
        string.Equals(providerName, SqlServerProvider, StringComparison.Ordinal);

    /// <summary>
    /// The collation for a column whose values are case-sensitive identifiers somewhere else.
    /// </summary>
    /// <remarks>
    /// <c>BIN2</c> rather than a <c>_CS_AS</c> collation: these are opaque identifiers compared for
    /// equality, never sorted for a human, so code-point ordering is exactly right and is also the
    /// cheapest comparison SQL Server has.
    /// </remarks>
    public const string SqlServerCaseSensitiveCollation = "Latin1_General_100_BIN2";

    /// <summary>
    /// Applies the mappings that differ per database, after the shared configuration.
    /// </summary>
    /// <param name="builder">The model builder.</param>
    /// <param name="providerName">
    /// <c>Database.ProviderName</c>. Null for a provider that is neither - a SQLite test, say -
    /// which keeps the SQL Server shape, because that is what those tests were written against.
    /// The collations below are the exception: their names are SQL Server's, so SQLite must not be
    /// given them. It compares text with BINARY by default, which is the behaviour they restore
    /// anyway, so a SQLite test already sees the intended semantics.
    /// </param>
    public static void ApplyProviderSpecifics(this ModelBuilder builder, string? providerName)
    {
        ArgumentNullException.ThrowIfNull(builder);

        if (IsSqlServer(providerName))
        {
            ApplySqlServerCaseSensitiveColumns(builder);
            return;
        }

        if (!IsPostgreSql(providerName)) return;

        ApplyPostgreSqlConcurrencyTokens(builder);
        ApplyPostgreSqlIndexFilters(builder);
    }

    /// <summary>
    /// Forces a case-sensitive collation on the columns that hold identifiers owned by a
    /// case-sensitive system.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A SQL Server instance's default collation is usually case-INsensitive, and every column
    /// inherits it. That is right for most text here - an operator searching for a connection by name
    /// should not have to match its casing - but wrong for two columns, and wrong in a way that a
    /// unique index turns into an outage rather than a curiosity.
    /// </para>
    /// <para>
    /// Git branch names are case-sensitive: <c>feature/Login</c> and <c>feature/login</c> are two
    /// different branches, and a repository can hold both. Under a CI collation
    /// <c>IX_RepositoryBranch_RepositoryId_Name</c> sees one duplicate key and rejects the second -
    /// inside a message consumer, which then redelivers and fails forever, taking every later branch
    /// update for that repository with it. Confirmed against SQL Server 2022, not inferred: two
    /// inserts differing only in case, <c>Msg 2601</c> on the second.
    /// </para>
    /// <para>
    /// The same holds for a repository's <c>ExternalId</c> - GitLab's <c>group/project</c> path is
    /// case-sensitive, and <c>IX_Repository_ConnectionId_ExternalId</c> is unique over it.
    /// </para>
    /// <para>
    /// PostgreSQL needs nothing here: its default collation already compares text case-sensitively,
    /// which is why this is a divergence rather than a shared mapping.
    /// </para>
    /// </remarks>
    private static void ApplySqlServerCaseSensitiveColumns(ModelBuilder builder)
    {
        builder.Entity<RepositoryBranch>()
            .Property(e => e.Name)
            .UseCollation(SqlServerCaseSensitiveCollation);

        builder.Entity<Repository>()
            .Property(e => e.ExternalId)
            .UseCollation(SqlServerCaseSensitiveCollation);
    }

    /// <summary>
    /// Swaps <c>rowversion</c> for PostgreSQL's <c>xmin</c>.
    /// </summary>
    /// <remarks>
    /// The entities carry <c>[Timestamp] byte[] RowVersion</c>, which is SQL Server's
    /// auto-maintained rowversion. Npgsql accepts that attribute without complaint and maps it to a
    /// plain <c>bytea</c> - a column PostgreSQL never writes. The model would build, the migration
    /// would scaffold, and optimistic concurrency would simply never fire: two replicas processing
    /// the same webhook would both win. Silence, not an error.
    ///
    /// PostgreSQL's equivalent is the system column <c>xmin</c>, which it bumps on every update.
    /// Npgsql's convention maps a <c>uint</c>, OnAddOrUpdate concurrency token onto it, so a shadow
    /// property is enough and the CLR entity stays as SQL Server wants it. Nothing reads
    /// RowVersion in code - it exists to be compared by EF - so ignoring it here costs nothing.
    /// </remarks>
    private static void ApplyPostgreSqlConcurrencyTokens(ModelBuilder builder)
    {
        builder.Entity<MergeRequest>().Ignore(e => e.RowVersion);
        builder.Entity<MergeRequest>().Property<uint>("xmin").IsRowVersion();

        builder.Entity<TaskItem>().Ignore(e => e.RowVersion);
        builder.Entity<TaskItem>().Property<uint>("xmin").IsRowVersion();

        // Every new RowVersion entity needs its own entry here, or Npgsql silently maps [Timestamp]
        // to an unwritten bytea and optimistic concurrency never fires on PostgreSQL. Moving to a
        // per-(MR, environment) claim and state doubles how many of these there are -- each is here.
        builder.Entity<RolloutPlan>().Ignore(e => e.RowVersion);
        builder.Entity<RolloutPlan>().Property<uint>("xmin").IsRowVersion();

        builder.Entity<Rollout>().Ignore(e => e.RowVersion);
        builder.Entity<Rollout>().Property<uint>("xmin").IsRowVersion();

        builder.Entity<RolloutStep>().Ignore(e => e.RowVersion);
        builder.Entity<RolloutStep>().Property<uint>("xmin").IsRowVersion();

        builder.Entity<MrDeploymentState>().Ignore(e => e.RowVersion);
        builder.Entity<MrDeploymentState>().Property<uint>("xmin").IsRowVersion();

        // The readiness pin is also per-(MR, environment) live state two operators can write at once,
        // and it declares the same [Timestamp] token -- so it needs the same swap, or the token it
        // carries to reject the second concurrent pin is an unwritten bytea on PostgreSQL and the
        // rejection never happens there while it does on SQL Server.
        builder.Entity<MergeRequestReadinessPin>().Ignore(e => e.RowVersion);
        builder.Entity<MergeRequestReadinessPin>().Property<uint>("xmin").IsRowVersion();

        // The planning settings are one row two administrators can open at once, and the later save
        // must not silently discard the earlier one. ModelMappingTests caught this entry missing when
        // the entity was added, which is what that test is for.
        builder.Entity<PlanningSettings>().Ignore(e => e.RowVersion);
        builder.Entity<PlanningSettings>().Property<uint>("xmin").IsRowVersion();
    }

    /// <summary>
    /// Rewrites the one filtered index, which is written in SQL Server's dialect.
    /// </summary>
    /// <remarks>
    /// A filter is a fragment of SQL, and EF passes it through verbatim: <c>[IsActive] = 1</c>
    /// reaches PostgreSQL as-is, where the brackets are not quoting and a boolean does not equal 1.
    /// That one at least fails loudly, when the migration is applied. Rewritten rather than dropped
    /// because the index is what stops two concurrent recalculations from leaving two active plans.
    /// </remarks>
    private static void ApplyPostgreSqlIndexFilters(ModelBuilder builder)
    {
        // One active rollout plan per target task -- the SQL Server filter "[IsActive] = 1" from
        // RolloutPlanConfiguration reaches PostgreSQL as-is otherwise, where the brackets are not
        // quoting and a boolean does not equal 1.
        builder.Entity<RolloutPlan>()
            .HasIndex(e => e.TargetTaskId)
            .IsUnique()
            .HasFilter("\"IsActive\"")
            .HasDatabaseName("IX_RolloutPlan_TargetTaskId_Active");

        // One live rollout per (task, environment) -- the SQL Server filter from RolloutConfiguration
        // reaches PostgreSQL with the wrong identifier quoting otherwise. The integer terminal set and
        // the <>/AND form are the same on both databases; only the brackets-to-quotes change.
        builder.Entity<Rollout>()
            .HasIndex(e => new { e.TargetTaskId, e.EnvironmentId })
            .IsUnique()
            .HasFilter("\"Status\" <> 5 AND \"Status\" <> 6 AND \"Status\" <> 7")
            .HasDatabaseName("IX_Rollout_TargetTask_Environment_Live");
    }
}
