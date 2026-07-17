using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using ReleaseOrchestrator.Infrastructure.Persistence;
using Xunit;

namespace ReleaseOrchestrator.UnitTests.Persistence;

/// <summary>
/// Guards the mapping as a whole, rather than one entity at a time.
/// </summary>
/// <remarks>
/// EF builds the model without touching a database, so this needs no server.
/// </remarks>
public class ModelMappingTests
{
    private static IModel BuildModel()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlServer("Server=model-only;Database=model-only;Trusted_Connection=True")
            .Options;

        using var context = new AppDbContext(options);
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
    /// unrelated test asked for a single index and got two.
    /// </remarks>
    [Fact]
    public void NoEntityDeclaresTheSameIndexTwice()
    {
        var duplicates = BuildModel().GetEntityTypes()
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
    [InlineData("TaskDependency", "DependentTask")]
    [InlineData("TaskDependency", "DependsOnTask")]
    [InlineData("StageItem", "MergeRequest")]
    [InlineData("MergeRequest", "Repository")]
    [InlineData("StackDependency", "FromStack")]
    [InlineData("StackDependency", "ToStack")]
    public void RelationshipRefusesToCascade(string entityName, string navigationName)
    {
        var entity = BuildModel().GetEntityTypes().Single(e => e.ClrType.Name == entityName);
        var navigation = entity.FindNavigation(navigationName)!;

        Assert.Equal(DeleteBehavior.Restrict, navigation.ForeignKey.DeleteBehavior);
    }
}
