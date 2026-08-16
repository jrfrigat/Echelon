using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Echelon.Application.ReleasePlanning;
using Echelon.Core.Enums;
using Echelon.Infrastructure.ReleasePlanning;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace Echelon.UnitTests.ReleasePlanning;

/// <summary>
/// Exporting a task's plan as YAML.
/// </summary>
/// <remarks>
/// Built on the shape from docs/issues/012: task-1 is an umbrella over task-2 and task-3, which are
/// unrelated to each other, and task-2 additionally waits on task-4. That arrangement is what makes
/// the export worth checking - it has a task with no merge requests of its own, two independent
/// branches of the tree, and a prerequisite reached only through a declared link.
/// </remarks>
public class PlanExportTests : PlannerTestBase
{
    private RolloutPlanner Planner() =>
        new(Db, new FakeTimeProvider(Now), NullLogger<RolloutPlanner>.Instance);

    /// <summary>Parses the export, which also proves it is well-formed YAML rather than near-YAML.</summary>
    private static YamlMappingNode Parse(string yaml)
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(yaml));
        return (YamlMappingNode)stream.Documents[0].RootNode;
    }

    private static string Scalar(YamlNode node, string key) =>
        ((YamlScalarNode)((YamlMappingNode)node).Children[new YamlScalarNode(key)]).Value!;

    private static IEnumerable<YamlMappingNode> Nodes(YamlMappingNode root) =>
        ((YamlSequenceNode)root.Children[new YamlScalarNode("nodes")]).Cast<YamlMappingNode>();

    [Fact]
    public async Task ATaskWithNoPlanExportsNothing()
    {
        var task = AddTask("PROJ-1");
        await Db.SaveChangesAsync(Ct);

        Assert.Null(await Planner().ExportPlanYamlAsync(task.Id, Ct));
    }

    [Fact]
    public async Task ExportsTheTreeAsParseableYaml()
    {
        var (target, _, _, _) = await BuildTreeAsync();

        var planner = Planner();
        await planner.RecalculateAsync(target.Id, actor: null, Ct);

        var yaml = await planner.ExportPlanYamlAsync(target.Id, Ct);
        Assert.NotNull(yaml);

        var root = Parse(yaml!);
        Assert.Equal("1", Scalar(root, "version"));
        Assert.Equal("TASK-1", Scalar(root, "target_task"));

        // Every task of the closure is a node, including the umbrella that has no merge requests.
        var keys = Nodes(root).Select(n => Scalar(n, "task")).ToList();
        Assert.Equal(["TASK-1", "TASK-2", "TASK-3", "TASK-4"], keys.Order(StringComparer.Ordinal));

        // The target sorts first: it is what the document is about.
        Assert.Equal("TASK-1", keys[0]);
    }

    [Fact]
    public async Task AMergeRequestIsNamedByItsNaturalKey()
    {
        var (target, _, _, _) = await BuildTreeAsync();

        var planner = Planner();
        await planner.RecalculateAsync(target.Id, actor: null, Ct);
        var root = Parse((await planner.ExportPlanYamlAsync(target.Id, Ct))!);

        var task4 = Nodes(root).Single(n => Scalar(n, "task") == "TASK-4");
        var mrs = (YamlSequenceNode)task4.Children[new YamlScalarNode("merge_requests")];

        // connection:repository!id -- resolvable back to a real merge request by a human reading it.
        Assert.Equal("vcs:group/auth!auth-1", Scalar(mrs.First(), "mr"));
    }

    [Fact]
    public async Task TheUmbrellaTaskCarriesNoMergeRequests()
    {
        // A parent that only groups work has nothing to deploy, so it is a node in the tree and not a
        // step in the rollout. The document has to show that rather than omit the task.
        var (target, _, _, _) = await BuildTreeAsync();

        var planner = Planner();
        await planner.RecalculateAsync(target.Id, actor: null, Ct);
        var root = Parse((await planner.ExportPlanYamlAsync(target.Id, Ct))!);

        var umbrella = Nodes(root).Single(n => Scalar(n, "task") == "TASK-1");
        Assert.False(umbrella.Children.ContainsKey(new YamlScalarNode("merge_requests")));
    }

    [Fact]
    public async Task PrerequisitesAreRecordedAsTaskKeys()
    {
        var (target, _, _, _) = await BuildTreeAsync();

        var planner = Planner();
        await planner.RecalculateAsync(target.Id, actor: null, Ct);
        var root = Parse((await planner.ExportPlanYamlAsync(target.Id, Ct))!);

        var umbrella = Nodes(root).Single(n => Scalar(n, "task") == "TASK-1");
        var dependsOn = ((YamlSequenceNode)umbrella.Children[new YamlScalarNode("depends_on")])
            .Select(n => ((YamlScalarNode)n).Value).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(["TASK-2", "TASK-3"], dependsOn);

        // Reached only through the declared link, which is what puts it in the closure at all.
        var task2 = Nodes(root).Single(n => Scalar(n, "task") == "TASK-2");
        var task2DependsOn = ((YamlSequenceNode)task2.Children[new YamlScalarNode("depends_on")])
            .Select(n => ((YamlScalarNode)n).Value).ToList();
        Assert.Equal(["TASK-4"], task2DependsOn);
    }

    [Fact]
    public async Task WavesReflectTheOrderingRepositoryRulesImpose()
    {
        var (target, _, db, web) = await BuildTreeAsync();

        // web deploys after db, so within task-2 the two merge requests land in different waves.
        AddRepositoryDependency(web, db, StackDependencyType.Hard);
        await Db.SaveChangesAsync(Ct);

        var planner = Planner();
        await planner.RecalculateAsync(target.Id, actor: null, Ct);
        var root = Parse((await planner.ExportPlanYamlAsync(target.Id, Ct))!);

        var task2 = Nodes(root).Single(n => Scalar(n, "task") == "TASK-2");
        var mrs = ((YamlSequenceNode)task2.Children[new YamlScalarNode("merge_requests")])
            .Cast<YamlMappingNode>()
            .ToDictionary(n => Scalar(n, "mr"), n => int.Parse(Scalar(n, "wave")));

        Assert.True(mrs["vcs:group/db!db-2"] < mrs["vcs:group/web!web-2"]);
    }

    /// <summary>
    /// task-1 over task-2 and task-3; task-2 waits on task-4. Repositories deliberately differ per
    /// task so a repository rule can order within one task without touching the other.
    /// </summary>
    private async Task<(Infrastructure.Persistence.Models.TaskItem Target,
        Infrastructure.Persistence.Models.TaskItem Task2,
        Infrastructure.Persistence.Models.Repository Db,
        Infrastructure.Persistence.Models.Repository Web)> BuildTreeAsync()
    {
        var task1 = AddTask("TASK-1");
        var task2 = AddTask("TASK-2");
        var task3 = AddTask("TASK-3");
        var task4 = AddTask("TASK-4");

        AddChild(task1, task2);
        AddChild(task1, task3);
        AddTaskDependency(task2, task4);

        var db = AddRepository("db");
        var web = AddRepository("web");
        var api = AddRepository("api");
        var reports = AddRepository("reports");
        var auth = AddRepository("auth");

        AddMergeRequest(db, task2, externalId: "db-2");
        AddMergeRequest(web, task2, externalId: "web-2");
        AddMergeRequest(api, task3, externalId: "api-3");
        AddMergeRequest(reports, task3, externalId: "reports-3");
        AddMergeRequest(auth, task4, externalId: "auth-1");

        await Db.SaveChangesAsync(Ct);
        return (task1, task2, db, web);
    }
}
