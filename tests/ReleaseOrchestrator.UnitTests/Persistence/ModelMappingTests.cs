using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using ReleaseOrchestrator.Infrastructure.Persistence;
using ReleaseOrchestrator.Infrastructure.Persistence.Models;
using Xunit;

namespace ReleaseOrchestrator.UnitTests.Persistence;

/// <summary>
/// Guards the mapping as a whole, on both databases.
/// </summary>
/// <remarks>
/// EF builds the model without touching a server, so every one of these runs offline — which is
/// what makes them the strongest check available for PostgreSQL, a database this environment
/// cannot start. They prove the model; only a live server proves the schema.
///
/// Everything here is a theory over both providers on purpose. The model is meant to be the same
/// on each except where ProviderSpecificMapping says otherwise, and an invariant asserted on one
/// provider is an invariant unchecked on the other.
/// </remarks>
public class ModelMappingTests
{
    public static TheoryData<string> Providers => new(DatabaseProviders.SqlServer, DatabaseProviders.PostgreSql);

    private static IModel BuildModel(string provider)
    {
        using var context = new AppDbContext(OptionsFor(provider));
        return context.Model;
    }

    /// <summary>
    /// The design-time model, which keeps the configuration the runtime model drops.
    /// </summary>
    /// <remarks>
    /// <c>context.Model</c> is read-optimized: anything only a migration needs — a collation, for
    /// instance — is stripped from it, and asking for it throws rather than answering null. That
    /// distinction matters here because a collation asserted against the wrong model would look like
    /// a mapping bug when it is only the wrong accessor.
    /// </remarks>
    private static IModel BuildDesignTimeModel(string provider)
    {
        using var context = new AppDbContext(OptionsFor(provider));
        return context.GetService<IDesignTimeModel>().Model;
    }

    private static DbContextOptions<AppDbContext> OptionsFor(string provider)
    {
        var builder = new DbContextOptionsBuilder<AppDbContext>();

        _ = provider switch
        {
            DatabaseProviders.SqlServer => builder.UseSqlServer("Server=model-only;Database=model-only;Trusted_Connection=True"),
            DatabaseProviders.PostgreSql => builder.UseNpgsql("Host=model-only;Database=model-only;Username=u;Password=p"),
            _ => throw new ArgumentOutOfRangeException(nameof(provider))
        };

        return builder.Options;
    }

    /// <summary>
    /// An index declared both by attribute and in fluent configuration is two indexes in the model
    /// and one in the database, because both carry the same database name.
    /// </summary>
    /// <remarks>
    /// This is worth a test of its own because `ef migrations has-pending-model-changes` cannot see
    /// it: the schema is identical, so the check that guards every other mapping change stays green
    /// while the model quietly holds two of everything. It happened — moving these declarations onto
    /// the models left two configuration files behind, and the duplicates went unnoticed until an
    /// unrelated test asked for a single index and got two. The PostgreSQL case is not theoretical
    /// either: its filtered index is declared a second time, in ProviderSpecificMapping.
    /// </remarks>
    [Theory]
    [MemberData(nameof(Providers))]
    public void NoEntityDeclaresTheSameIndexTwice(string provider)
    {
        var duplicates = BuildModel(provider).GetEntityTypes()
            .SelectMany(entity => entity.GetIndexes()
                .GroupBy(index => string.Join(", ", index.Properties.Select(p => p.Name)))
                .Where(sameProperties => sameProperties.Count() > 1)
                .Select(sameProperties => $"{entity.ClrType.Name}({sameProperties.Key})"))
            .ToList();

        Assert.Empty(duplicates);
    }

    /// <summary>
    /// Restrict is not the convention — a required relationship defaults to Cascade — so every one
    /// of these is a deliberate refusal to let a delete propagate, and losing one silently turns a
    /// blocked delete into a successful one that takes rows with it.
    /// </summary>
    [Theory]
    [InlineData(DatabaseProviders.SqlServer, "TaskDependency", "DependentTask")]
    [InlineData(DatabaseProviders.SqlServer, "TaskDependency", "DependsOnTask")]
    // Self-referencing, and not merely a preference: SQL Server rejects a cascade or set-null action
    // that targets the same table, so Restrict is the only behaviour the hierarchy can have.
    [InlineData(DatabaseProviders.SqlServer, "TaskItem", "ParentTask")]
    [InlineData(DatabaseProviders.PostgreSql, "TaskItem", "ParentTask")]
    [InlineData(DatabaseProviders.SqlServer, "MergeRequest", "Repository")]
    [InlineData(DatabaseProviders.SqlServer, "PlanItem", "MergeRequest")]
    [InlineData(DatabaseProviders.SqlServer, "RolloutStep", "MergeRequest")]
    [InlineData(DatabaseProviders.SqlServer, "MrDeploymentState", "MergeRequest")]
    [InlineData(DatabaseProviders.PostgreSql, "TaskDependency", "DependentTask")]
    [InlineData(DatabaseProviders.PostgreSql, "TaskDependency", "DependsOnTask")]
    [InlineData(DatabaseProviders.PostgreSql, "MergeRequest", "Repository")]
    [InlineData(DatabaseProviders.PostgreSql, "PlanItem", "MergeRequest")]
    [InlineData(DatabaseProviders.PostgreSql, "RolloutStep", "MergeRequest")]
    [InlineData(DatabaseProviders.PostgreSql, "MrDeploymentState", "MergeRequest")]
    public void RelationshipRefusesToCascade(string provider, string entityName, string navigationName)
    {
        var entity = BuildModel(provider).GetEntityTypes().Single(e => e.ClrType.Name == entityName);
        var navigation = entity.FindNavigation(navigationName)!;

        Assert.Equal(DeleteBehavior.Restrict, navigation.ForeignKey.DeleteBehavior);
    }

    /// <summary>
    /// The one active plan PER task is enforced by a filtered unique index, and a filter is a
    /// fragment of SQL that EF passes through verbatim — so it is the mapping most obviously not
    /// portable.
    /// </summary>
    /// <remarks>
    /// SQL Server's <c>[IsActive] = 1</c> reaching PostgreSQL is a syntax error twice over: the
    /// brackets are not quoting there, and a boolean does not compare to 1. It fails when the
    /// migration is applied, which is late but loud. The index itself is not optional — without it
    /// two concurrent recalculations leave two active plans for one task — so it is rewritten per
    /// provider rather than dropped.
    /// </remarks>
    [Theory]
    [InlineData(DatabaseProviders.SqlServer, "[IsActive] = 1")]
    [InlineData(DatabaseProviders.PostgreSql, "\"IsActive\"")]
    public void TheOneActivePlanPerTaskIndexIsFilteredInEachDatabasesOwnDialect(string provider, string expectedFilter)
    {
        var index = BuildModel(provider)
            .FindEntityType(typeof(RolloutPlan))!
            .GetIndexes()
            .Single(i => i.GetDatabaseName() == "IX_RolloutPlan_TargetTaskId_Active");

        Assert.True(index.IsUnique);
        Assert.Equal(expectedFilter, index.GetFilter());
        Assert.Equal("IX_RolloutPlan_TargetTaskId_Active", index.GetDatabaseName());
    }

    /// <summary>
    /// Concurrency is a real token on both, by different means — for every entity that carries one.
    /// </summary>
    /// <remarks>
    /// This is the mapping that fails silently, which is why it is asserted rather than trusted.
    /// The entities carry <c>[Timestamp] byte[] RowVersion</c> — SQL Server's auto-maintained
    /// rowversion. Npgsql accepts that attribute and maps it to a plain <c>bytea</c>: a column
    /// PostgreSQL never writes, so the token is always null and the concurrency check never fires.
    /// No error, no warning — two replicas processing the same webhook would both win.
    ///
    /// PostgreSQL's equivalent is the system column <c>xmin</c>, which it bumps on every update, so
    /// the model uses that and drops RowVersion entirely.
    ///
    /// The cases are discovered by reflection over the model, not hard-coded, on purpose: a hard-coded
    /// list is exactly what let a new RowVersion entity ship unmapped once. Every entity SQL Server
    /// gives a RowVersion token is required to get an xmin token on PostgreSQL — so forgetting the
    /// <c>ProviderSpecificMapping</c> entry for the next one fails here instead of in production on
    /// the second database.
    /// </remarks>
    [Theory]
    [MemberData(nameof(RowVersionEntities))]
    public void EachDatabaseGetsAConcurrencyTokenItActuallyMaintains(string entityName)
    {
        var sqlServer = BuildModel(DatabaseProviders.SqlServer).GetEntityTypes().Single(e => e.ClrType.Name == entityName);
        var postgres = BuildModel(DatabaseProviders.PostgreSql).GetEntityTypes().Single(e => e.ClrType.Name == entityName);

        var onSqlServer = Assert.Single(sqlServer.GetProperties(), p => p.IsConcurrencyToken);
        Assert.Equal("RowVersion", onSqlServer.Name);
        Assert.Equal(ValueGenerated.OnAddOrUpdate, onSqlServer.ValueGenerated);

        var onPostgres = Assert.Single(postgres.GetProperties(), p => p.IsConcurrencyToken);
        Assert.Equal("xmin", onPostgres.GetColumnName());
        Assert.Equal(typeof(uint), onPostgres.ClrType);
        Assert.Equal(ValueGenerated.OnAddOrUpdate, onPostgres.ValueGenerated);

        // The bytea that PostgreSQL would never fill must not survive alongside it.
        Assert.Null(postgres.FindProperty("RowVersion"));
    }

    /// <summary>
    /// The columns holding identifiers that a case-sensitive system owns must compare
    /// case-sensitively on SQL Server too.
    /// </summary>
    /// <remarks>
    /// A SQL Server instance's default collation is normally case-insensitive and every column
    /// inherits it, which is right for most text here and wrong for these two — both sit under a
    /// unique index, so a CI collation turns two legitimately different values into a duplicate key.
    /// Confirmed on SQL Server 2022 before it was fixed: inserting <c>feature/Login</c> then
    /// <c>feature/login</c> gave <c>Msg 2601</c>, and inside a consumer that is a message which
    /// redelivers and fails forever.
    ///
    /// Asserted on the model rather than trusted because nothing else can see it: SQLite (what the
    /// other tests run on) compares with BINARY and so behaves correctly by accident, and
    /// <c>has-pending-model-changes</c> stays green either way once the migration exists.
    /// </remarks>
    [Theory]
    [InlineData(nameof(RepositoryBranch), nameof(RepositoryBranch.Name))]
    [InlineData(nameof(Repository), nameof(Repository.ExternalId))]
    public void CaseSensitiveIdentifiersGetABinaryCollationOnSqlServer(string entityName, string propertyName)
    {
        var property = BuildDesignTimeModel(DatabaseProviders.SqlServer)
            .GetEntityTypes().Single(e => e.ClrType.Name == entityName)
            .GetProperties().Single(p => p.Name == propertyName);

        Assert.Equal("Latin1_General_100_BIN2", property.GetCollation());
    }

    /// <summary>
    /// PostgreSQL must NOT carry the SQL Server collation name — it already compares text
    /// case-sensitively, and the name would not resolve there.
    /// </summary>
    [Theory]
    [InlineData(nameof(RepositoryBranch), nameof(RepositoryBranch.Name))]
    [InlineData(nameof(Repository), nameof(Repository.ExternalId))]
    public void PostgreSqlIsLeftOnItsOwnCollation(string entityName, string propertyName)
    {
        var property = BuildDesignTimeModel(DatabaseProviders.PostgreSql)
            .GetEntityTypes().Single(e => e.ClrType.Name == entityName)
            .GetProperties().Single(p => p.Name == propertyName);

        Assert.Null(property.GetCollation());
    }

    /// <summary>
    /// Every entity the SQL Server model gives a <c>RowVersion</c> concurrency token — the set that
    /// must each be remapped to <c>xmin</c> for PostgreSQL.
    /// </summary>
    public static TheoryData<string> RowVersionEntities()
    {
        var data = new TheoryData<string>();
        foreach (var entity in BuildModel(DatabaseProviders.SqlServer).GetEntityTypes())
        {
            if (entity.GetProperties().Any(p => p.IsConcurrencyToken && p.Name == "RowVersion"))
                data.Add(entity.ClrType.Name);
        }

        return data;
    }
}
