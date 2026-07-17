using Microsoft.EntityFrameworkCore;
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
        var builder = new DbContextOptionsBuilder<AppDbContext>();

        _ = provider switch
        {
            DatabaseProviders.SqlServer => builder.UseSqlServer("Server=model-only;Database=model-only;Trusted_Connection=True"),
            DatabaseProviders.PostgreSql => builder.UseNpgsql("Host=model-only;Database=model-only;Username=u;Password=p"),
            _ => throw new ArgumentOutOfRangeException(nameof(provider))
        };

        using var context = new AppDbContext(builder.Options);
        return context.Model;
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
    [InlineData(DatabaseProviders.SqlServer, "StageItem", "MergeRequest")]
    [InlineData(DatabaseProviders.SqlServer, "MergeRequest", "Repository")]
    [InlineData(DatabaseProviders.SqlServer, "StackDependency", "FromStack")]
    [InlineData(DatabaseProviders.SqlServer, "StackDependency", "ToStack")]
    [InlineData(DatabaseProviders.PostgreSql, "TaskDependency", "DependentTask")]
    [InlineData(DatabaseProviders.PostgreSql, "TaskDependency", "DependsOnTask")]
    [InlineData(DatabaseProviders.PostgreSql, "StageItem", "MergeRequest")]
    [InlineData(DatabaseProviders.PostgreSql, "MergeRequest", "Repository")]
    [InlineData(DatabaseProviders.PostgreSql, "StackDependency", "FromStack")]
    [InlineData(DatabaseProviders.PostgreSql, "StackDependency", "ToStack")]
    public void RelationshipRefusesToCascade(string provider, string entityName, string navigationName)
    {
        var entity = BuildModel(provider).GetEntityTypes().Single(e => e.ClrType.Name == entityName);
        var navigation = entity.FindNavigation(navigationName)!;

        Assert.Equal(DeleteBehavior.Restrict, navigation.ForeignKey.DeleteBehavior);
    }

    /// <summary>
    /// The one active plan is enforced by a filtered unique index, and a filter is a fragment of
    /// SQL that EF passes through verbatim — so it is the mapping most obviously not portable.
    /// </summary>
    /// <remarks>
    /// SQL Server's <c>[IsActive] = 1</c> reaching PostgreSQL is a syntax error twice over: the
    /// brackets are not quoting there, and a boolean does not compare to 1. It fails when the
    /// migration is applied, which is late but loud. The index itself is not optional — without it
    /// two concurrent recalculations leave two active plans — so it is rewritten per provider
    /// rather than dropped.
    /// </remarks>
    [Theory]
    [InlineData(DatabaseProviders.SqlServer, "[IsActive] = 1")]
    [InlineData(DatabaseProviders.PostgreSql, "\"IsActive\"")]
    public void TheOneActivePlanIndexIsFilteredInEachDatabasesOwnDialect(string provider, string expectedFilter)
    {
        var index = BuildModel(provider)
            .FindEntityType(typeof(ReleasePlan))!
            .GetIndexes()
            .Single(i => i.Properties.Any(p => p.Name == nameof(ReleasePlan.IsActive)));

        Assert.True(index.IsUnique);
        Assert.Equal(expectedFilter, index.GetFilter());
        Assert.Equal("IX_ReleasePlan_IsActive", index.GetDatabaseName());
    }

    /// <summary>
    /// Concurrency is a real token on both, by different means.
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
    /// </remarks>
    [Theory]
    [InlineData(typeof(MergeRequest))]
    [InlineData(typeof(TaskItem))]
    public void EachDatabaseGetsAConcurrencyTokenItActuallyMaintains(Type entityType)
    {
        var sqlServer = BuildModel(DatabaseProviders.SqlServer).FindEntityType(entityType)!;
        var postgres = BuildModel(DatabaseProviders.PostgreSql).FindEntityType(entityType)!;

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
}
