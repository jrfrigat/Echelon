using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Echelon.Infrastructure.Persistence.Models;
using Echelon.Infrastructure.Persistence;
using Xunit;

namespace Echelon.UnitTests;

/// <summary>
/// Locks down which foreign key backs each dependency navigation.
/// <see cref="ReleasePlanGraphTests"/> hand-wires navigations, so it proves the algorithm
/// but not the mapping - and the mapping is precisely what was inverted: every task
/// dependency collapsed into a self-loop, stranding the MR that should have deployed first.
/// EF builds the model without touching the database, so no server is needed here.
/// </summary>
public class TaskDependencyMappingTests
{
    private static IModel BuildModel()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlServer("Server=model-only;Database=model-only;Trusted_Connection=True")
            .Options;

        using var context = new AppDbContext(options);
        return context.Model;
    }

    /// <summary>A row (DependentTaskId = B, DependsOnTaskId = A) means "B depends on A".</summary>
    [Fact]
    public void DependenciesNavigationIsKeyedByDependentTaskId()
    {
        var navigation = BuildModel()
            .FindEntityType(typeof(TaskItem))!
            .FindNavigation(nameof(TaskItem.Dependencies))!;

        Assert.Equal(
            nameof(TaskDependency.DependentTaskId),
            Assert.Single(navigation.ForeignKey.Properties).Name);
    }

    [Fact]
    public void DependentsNavigationIsKeyedByDependsOnTaskId()
    {
        var navigation = BuildModel()
            .FindEntityType(typeof(TaskItem))!
            .FindNavigation(nameof(TaskItem.Dependents))!;

        Assert.Equal(
            nameof(TaskDependency.DependsOnTaskId),
            Assert.Single(navigation.ForeignKey.Properties).Name);
    }

    [Fact]
    public void MergeRequestNaturalKeyIsUnique()
    {
        var index = BuildModel()
            .FindEntityType(typeof(MergeRequest))!
            .GetIndexes()
            .Single(i => i.Properties.Select(p => p.Name)
                .SequenceEqual([nameof(MergeRequest.RepositoryId), nameof(MergeRequest.ExternalId)]));

        Assert.True(index.IsUnique);
    }

    [Fact]
    public void TaskNaturalKeyIsUnique()
    {
        var index = BuildModel()
            .FindEntityType(typeof(TaskItem))!
            .GetIndexes()
            .Single(i => i.Properties.Select(p => p.Name)
                .SequenceEqual([nameof(TaskItem.TrackerConnectionId), nameof(TaskItem.ExternalId)]));

        Assert.True(index.IsUnique);
    }
}
