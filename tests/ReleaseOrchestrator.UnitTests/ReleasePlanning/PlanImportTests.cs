using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using ReleaseOrchestrator.Application.ReleasePlanning;
using ReleaseOrchestrator.Core.Enums;
using ReleaseOrchestrator.Infrastructure.Persistence.Models;
using ReleaseOrchestrator.Infrastructure.ReleasePlanning;
using Xunit;

namespace ReleaseOrchestrator.UnitTests.ReleasePlanning;

/// <summary>
/// Validating and importing a plan document, and the invariant the whole design rests on: import,
/// hand edit and recalculate derive the same plan (006 §1).
/// </summary>
public class PlanImportTests : PlannerTestBase
{
    private RolloutPlanner NewPlanner() =>
        new(Db, new FakeTimeProvider(Now), NullLogger<RolloutPlanner>.Instance);

    /// <summary>Three tasks, two repositories: enough for waves that can actually be reordered.</summary>
    private async Task<(TaskItem Target, MergeRequest First, MergeRequest Second)> ArrangeAsync()
    {
        var parent = AddTask("PROJ-1");
        var child = AddTask("PROJ-2");
        AddChild(parent, child);

        var api = AddRepository("api");
        var web = AddRepository("web");

        var childMr = AddMergeRequest(web, child, createdAt: Now.AddDays(-2));
        var parentMr = AddMergeRequest(api, parent, createdAt: Now.AddDays(-1));

        await Db.SaveChangesAsync(Ct);
        return (parent, childMr, parentMr);
    }

    private static string KeyOf(MergeRequest mr, string repository) => $"vcs:group/{repository}!{mr.ExternalId}";

    [Fact]
    public async Task ExportedPlanImportsBackUnchanged()
    {
        var (target, _, _) = await ArrangeAsync();
        var planner = NewPlanner();
        await planner.RecalculateAsync(target.Id, actor: null, Ct);

        var exported = await planner.ExportPlanYamlAsync(target.Id, Ct);

        var result = await planner.ImportPlanAsync(target.Id, exported!, force: false, actor: null, Ct);

        Assert.True(result.Accepted);
        Assert.Empty(result.Errors);
        Assert.Empty(result.Violations);

        // The point of the round trip: what comes back out is what went in, minus the version stamp.
        var reExported = await planner.ExportPlanYamlAsync(target.Id, Ct);
        Assert.Equal(WithoutPlanVersion(exported!), WithoutPlanVersion(reExported!));
    }

    /// <summary>
    /// Import, hand edit and recalculate must produce the same plan - the invariant of 006 §1.
    /// </summary>
    /// <remarks>
    /// Stated as: reorder by importing a document, then recalculate as an ingestion event would, and
    /// the order must not move. If import stored its result as a plan instead of as deltas, the
    /// recalculation would quietly undo it, and the first webhook after an import would deploy in an
    /// order nobody chose.
    /// </remarks>
    [Fact]
    public async Task ImportSurvivesRecalculation()
    {
        var (target, childMr, parentMr) = await ArrangeAsync();
        var planner = NewPlanner();
        var derived = await planner.RecalculateAsync(target.Id, actor: null, Ct);

        // Stated so the test can fail: the derivation puts the child first, because a parent waits on
        // its subtask. An import that quietly did nothing would leave exactly this, and the assertions
        // below would still be checking something.
        Assert.Equal(1, WaveOf(derived, childMr.Id));
        Assert.Equal(2, WaveOf(derived, parentMr.Id));

        var document = Document(target.ExternalId,
            (target.ExternalId, KeyOf(parentMr, "api"), 1),
            ("PROJ-2", KeyOf(childMr, "web"), 2));

        var imported = await planner.ImportPlanAsync(target.Id, document, force: true, actor: null, Ct);
        Assert.True(imported.Accepted);

        // Stored as deltas, not as a plan: that is what makes the recalculation below reproduce it.
        Assert.NotEmpty(await Db.PlanOverrides.ToListAsync(Ct));
        Assert.Equal(1, WaveOf(imported.Plan!, parentMr.Id));
        Assert.Equal(2, WaveOf(imported.Plan!, childMr.Id));

        var recalculated = await planner.RecalculateAsync(target.Id, actor: null, Ct);

        Assert.Equal(1, WaveOf(recalculated, parentMr.Id));
        Assert.Equal(2, WaveOf(recalculated, childMr.Id));
    }

    /// <summary>
    /// An order that breaks a task dependency is refused, and accepted under force - but the plan
    /// records the breach either way. A plan may deploy against a constraint; it may never look clean
    /// while doing so.
    /// </summary>
    [Fact]
    public async Task ReversingATaskDependencyNeedsForceAndIsRecorded()
    {
        var (target, childMr, parentMr) = await ArrangeAsync();
        var planner = NewPlanner();
        await planner.RecalculateAsync(target.Id, actor: null, Ct);

        var document = Document(target.ExternalId,
            (target.ExternalId, KeyOf(parentMr, "api"), 1),
            ("PROJ-2", KeyOf(childMr, "web"), 2));

        var refused = await planner.ValidatePlanAsync(target.Id, document, Ct);
        Assert.False(refused.Accepted);
        var violation = Assert.Single(refused.Violations);
        Assert.Equal(nameof(PlanEdgeKind.TaskDependency), violation.Kind);
        Assert.Equal(KeyOf(childMr, "web"), violation.From);
        Assert.Equal(KeyOf(parentMr, "api"), violation.To);

        // Refused means nothing was written, not "written and complained about".
        Assert.Empty(await Db.PlanOverrides.ToListAsync(Ct));

        var forced = await planner.ImportPlanAsync(target.Id, document, force: true, actor: null, Ct);
        Assert.True(forced.Accepted);
        Assert.Single(forced.Plan!.Conflicts);
        Assert.Equal(nameof(PlanEdgeKind.TaskDependency), forced.Plan!.Conflicts[0].Kind);
    }

    [Fact]
    public async Task ImportRecordsItsSourceAndTheDocumentHash()
    {
        var (target, _, _) = await ArrangeAsync();
        var planner = NewPlanner();
        await planner.RecalculateAsync(target.Id, actor: null, Ct);
        var document = (await planner.ExportPlanYamlAsync(target.Id, Ct))!;

        await planner.ImportPlanAsync(target.Id, document, force: false, actor: null, Ct);

        var stored = await Db.RolloutPlans.AsNoTracking()
            .FirstAsync(p => p.TargetTaskId == target.Id && p.IsActive, Ct);

        Assert.Equal(PlanSource.Imported, stored.Source);
        Assert.NotNull(stored.YamlHash);
    }

    [Fact]
    public async Task DocumentPostedToTheWrongTaskIsRefused()
    {
        var (target, childMr, parentMr) = await ArrangeAsync();
        var other = AddTask("PROJ-9");
        await Db.SaveChangesAsync(Ct);

        var planner = NewPlanner();
        await planner.RecalculateAsync(target.Id, actor: null, Ct);
        await planner.RecalculateAsync(other.Id, actor: null, Ct);

        var document = Document(target.ExternalId,
            (target.ExternalId, KeyOf(parentMr, "api"), 1),
            ("PROJ-2", KeyOf(childMr, "web"), 2));

        var result = await planner.ValidatePlanAsync(other.Id, document, Ct);

        Assert.False(result.Accepted);
        Assert.Contains(result.Errors, e => e.Contains("posted to 'PROJ-9'"));
    }

    /// <summary>Membership belongs to the atlas: a document that leaves work out describes another plan.</summary>
    [Fact]
    public async Task DocumentMissingAMergeRequestIsRefused()
    {
        var (target, _, parentMr) = await ArrangeAsync();
        var planner = NewPlanner();
        await planner.RecalculateAsync(target.Id, actor: null, Ct);

        var document = Document(target.ExternalId, (target.ExternalId, KeyOf(parentMr, "api"), 1));

        var result = await planner.ValidatePlanAsync(target.Id, document, Ct);

        Assert.False(result.Accepted);
        Assert.Contains(result.Errors, e => e.Contains("missing from the document"));
    }

    [Fact]
    public async Task DocumentNamingUnknownWorkIsRefused()
    {
        var (target, childMr, parentMr) = await ArrangeAsync();
        var planner = NewPlanner();
        await planner.RecalculateAsync(target.Id, actor: null, Ct);

        var document = Document(target.ExternalId,
            (target.ExternalId, KeyOf(parentMr, "api"), 1),
            ("PROJ-2", KeyOf(childMr, "web"), 2))
            .Replace(KeyOf(childMr, "web"), "vcs:group/web!999", StringComparison.Ordinal);

        var result = await planner.ValidatePlanAsync(target.Id, document, Ct);

        Assert.False(result.Accepted);
        Assert.Contains(result.Errors, e => e.Contains("Unknown merge request"));
    }

    /// <summary>A gap in the waves means an empty stage: what was asked for and what would run differ.</summary>
    [Fact]
    public async Task WavesWithAGapAreRefused()
    {
        var (target, childMr, parentMr) = await ArrangeAsync();
        var planner = NewPlanner();
        await planner.RecalculateAsync(target.Id, actor: null, Ct);

        var document = Document(target.ExternalId,
            ("PROJ-2", KeyOf(childMr, "web"), 1),
            (target.ExternalId, KeyOf(parentMr, "api"), 3));

        var result = await planner.ValidatePlanAsync(target.Id, document, Ct);

        Assert.False(result.Accepted);
        Assert.Contains(result.Errors, e => e.Contains("no gaps"));
    }

    [Fact]
    public async Task ValidateWritesNothing()
    {
        var (target, _, _) = await ArrangeAsync();
        var planner = NewPlanner();
        var built = await planner.RecalculateAsync(target.Id, actor: null, Ct);
        var document = (await planner.ExportPlanYamlAsync(target.Id, Ct))!;

        var result = await planner.ValidatePlanAsync(target.Id, document, Ct);

        Assert.True(result.Accepted);
        Assert.Null(result.Plan);
        Assert.Empty(await Db.PlanOverrides.ToListAsync(Ct));

        var current = await Db.RolloutPlans.AsNoTracking()
            .FirstAsync(p => p.TargetTaskId == target.Id && p.IsActive, Ct);
        Assert.Equal(built.Version, current.Version);
    }

    private static int WaveOf(Application.DTOs.RolloutPlanDto plan, Guid mergeRequestId) =>
        plan.Nodes.SelectMany(n => n.Items).First(i => i.MergeRequestId == mergeRequestId).Wave;

    /// <summary>
    /// The plan version changes on every store, so comparing two exports has to ignore it.
    /// </summary>
    private static string WithoutPlanVersion(string document) =>
        string.Join('\n', document.Split('\n').Where(line => !line.StartsWith("plan_version:", StringComparison.Ordinal)));

    /// <summary>Writes a plan document by hand, so a test states the waves it means rather than editing text.</summary>
    private static string Document(string targetTask, params (string Task, string Mr, int Wave)[] items)
    {
        var text = new System.Text.StringBuilder()
            .AppendLine("version: 1")
            .AppendLine($"target_task: {targetTask}")
            .AppendLine("nodes:");

        foreach (var group in items.GroupBy(i => i.Task))
        {
            text.AppendLine($"  - task: {group.Key}").AppendLine("    merge_requests:");
            foreach (var item in group)
                text.AppendLine($"      - mr: {item.Mr}").AppendLine($"        wave: {item.Wave}");
        }

        return text.ToString();
    }
}
