using Echelon.Application.DTOs;
using Echelon.Infrastructure.ReleasePlanning;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Echelon.UnitTests.ReleasePlanning;

/// <summary>
/// The task list's column filters, which run in the database.
/// </summary>
/// <remarks>
/// They run there because the browser holds one page: filtering in the grid searches the slice and
/// presents the answer as though it had searched the list. The count has to be filtered with it - a
/// filtered page beside an unfiltered total offers pages that come back empty - which is why both go
/// through one <see cref="TaskListQuery"/>.
/// </remarks>
public class TaskListFilterTests : PlannerTestBase
{
    private RolloutPlanner Planner() =>
        new(Db, new FakeTimeProvider(Now), NullLogger<RolloutPlanner>.Instance);

    private async Task SeedAsync()
    {
        var one = AddTask("ECH-1");
        one.Title = "Rework the ordering rules";
        one.Status = "open";

        var two = AddTask("ECH-2");
        two.Title = "Fix the poller";
        two.Status = "inProgress";

        var three = AddTask("OPS-7");
        three.Title = "Rotate the tokens";
        three.Status = "open";

        await Db.SaveChangesAsync(Ct);
    }

    [Fact]
    public async Task NoFilterListsEverything()
    {
        await SeedAsync();
        var query = new TaskListQuery(1, 50);

        Assert.Equal(3, await Planner().CountTasksAsync(query, Ct));
        Assert.Equal(3, (await Planner().ListTasksAsync(query, Ct)).Count);
    }

    [Fact]
    public async Task FiltersOnPartOfTheKey()
    {
        await SeedAsync();
        var query = new TaskListQuery(1, 50, Key: "ECH");

        var items = await Planner().ListTasksAsync(query, Ct);

        Assert.Equal(["ECH-1", "ECH-2"], items.Select(t => t.ExternalId));
    }

    [Fact]
    public async Task FiltersOnPartOfTheTitle()
    {
        await SeedAsync();

        var items = await Planner().ListTasksAsync(new TaskListQuery(1, 50, Title: "poller"), Ct);

        Assert.Equal("ECH-2", Assert.Single(items).ExternalId);
    }

    [Fact]
    public async Task IgnoresCase()
    {
        await SeedAsync();

        // The database decides this by collation otherwise: SQL Server ignores case and PostgreSQL
        // does not, so the same box would match on one provider and not the other.
        var items = await Planner().ListTasksAsync(new TaskListQuery(1, 50, Key: "ech-1"), Ct);

        Assert.Equal("ECH-1", Assert.Single(items).ExternalId);
    }

    [Fact]
    public async Task CombinesTheBoxesWithAnd()
    {
        await SeedAsync();

        var items = await Planner().ListTasksAsync(
            new TaskListQuery(1, 50, Key: "ECH", Status: "open"), Ct);

        Assert.Equal("ECH-1", Assert.Single(items).ExternalId);
    }

    [Fact]
    public async Task CountsWhatTheFilterSelects()
    {
        await SeedAsync();
        var query = new TaskListQuery(1, 50, Status: "open");

        // The pager divides this by the page size. Counting the whole table here would offer pages
        // that come back empty, which is exactly what the shared query prevents.
        Assert.Equal(2, await Planner().CountTasksAsync(query, Ct));
    }

    [Fact]
    public async Task ABlankBoxIsNotAFilter()
    {
        await SeedAsync();

        // The grid clears a box by sending it empty; if that were a search for the empty string the
        // list would come back empty with nothing on screen to explain why.
        var items = await Planner().ListTasksAsync(new TaskListQuery(1, 50, Key: "   "), Ct);

        Assert.Equal(3, items.Count);
    }

    [Fact]
    public async Task PagesWithinTheFilteredSet()
    {
        await SeedAsync();

        var first = await Planner().ListTasksAsync(new TaskListQuery(1, 1, Key: "ECH"), Ct);
        var second = await Planner().ListTasksAsync(new TaskListQuery(2, 1, Key: "ECH"), Ct);

        Assert.Equal("ECH-1", Assert.Single(first).ExternalId);
        Assert.Equal("ECH-2", Assert.Single(second).ExternalId);
    }
}
