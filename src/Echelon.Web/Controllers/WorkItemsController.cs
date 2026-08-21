using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Echelon.Application.DTOs;
using Echelon.Core.Enums;
using Echelon.Core.Parsing;
using Echelon.Infrastructure.Auth;
using Echelon.Infrastructure.Persistence;

namespace Echelon.Web.Controllers;

/// <summary>
/// What there is to deploy, per task and repository.
/// </summary>
/// <remarks>
/// <para>
/// A connector does not really report merge requests - it reports that a task has work in a
/// repository, and a merge request is only the vehicle that work currently rides in. Before it is
/// raised, the vehicle is a branch; the task and the repository are the same either way. So the row
/// here is (task, repository), and the merge request or branch is what carries it.
/// </para>
/// <para>
/// A merge-request-only list could not show the earliest and most interesting state: a branch that
/// names a task and has no merge request yet. That is work in progress the plan cannot see, and it is
/// what the launch guard refuses a rollout over - so it belongs in the same list as everything else,
/// not hidden behind an error message at launch.
/// </para>
/// </remarks>
[ApiController]
[Route("api/work-items")]
[Authorize(Policy = Permissions.ReleasePlanView)]
public class WorkItemsController(AppDbContext db) : ControllerBase
{
    /// <summary>
    /// A hard ceiling on how much is read before paging. Beyond this the answer says it is partial
    /// rather than quietly showing a slice as though it were everything.
    /// </summary>
    private const int ScanCap = 5000;

    /// <summary>Deployable work, newest first, optionally judged against one environment.</summary>
    /// <param name="environmentId">Environment to report readiness for, or omit for none.</param>
    /// <param name="state">Optional state filter: <c>New</c>, <c>Opened</c>, <c>Merged</c>, <c>Closed</c>.</param>
    /// <param name="search">Free text matched against the task key, repository and branch.</param>
    /// <param name="taskKey">Substring of the task key; the grid's "task" column filter.</param>
    /// <param name="repository">Substring of the repository name; the grid's "repo" column filter.</param>
    /// <param name="connection">Substring of the connection name; the grid's "connection" column filter.</param>
    /// <param name="branch">Substring of the branch or carrier; the grid's "work" column filter.</param>
    /// <param name="page">1-based page.</param>
    /// <param name="pageSize">Page size, clamped by <see cref="Paging"/>.</param>
    /// <param name="ct">Cancellation token.</param>
    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] Guid? environmentId = null,
        [FromQuery] string? state = null,
        [FromQuery] string? search = null,
        [FromQuery] string? taskKey = null,
        [FromQuery] string? repository = null,
        [FromQuery] string? connection = null,
        [FromQuery] string? branch = null,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = Paging.DefaultPageSize,
        CancellationToken ct = default)
    {
        var paging = Paging.From(page, pageSize);

        var mergeRequests = await db.MergeRequests
            .OrderByDescending(m => m.CreatedAt)
            .Take(ScanCap)
            .Select(m => new
            {
                m.Id, m.ExternalId, m.SourceBranch, m.RepositoryId, m.Status, m.Labels,
                m.PipelineResult, m.IsStatusManual, m.CreatedAt,
                RepositoryName = m.Repository.Name,
                ConnectionName = m.Repository.Connection.Name,
                TaskKey = m.TaskExternalId
            })
            .AsNoTracking()
            .ToListAsync(ct);

        // Only branches that are actually outstanding work: a merged branch has landed and the
        // default branch is nobody's task.
        var branches = await db.RepositoryBranches
            .Where(b => !b.IsMerged && !b.IsDefault && b.TaskExternalId != null)
            .OrderByDescending(b => b.FirstSeenAt)
            .Take(ScanCap)
            .Select(b => new
            {
                b.Name, b.RepositoryId, b.TaskExternalId, b.FirstSeenAt,
                RepositoryName = b.Repository.Name,
                ConnectionName = b.Repository.Connection.Name
            })
            .AsNoTracking()
            .ToListAsync(ct);

        var readiness = await LoadReadinessAsync(environmentId, ct);

        var rows = mergeRequests
            .Select(m => new WorkItemDto(
                Kind: WorkItemKind.MergeRequest,
                TaskKey: m.TaskKey,
                RepositoryName: m.RepositoryName,
                ConnectionName: m.ConnectionName,
                Carrier: m.ExternalId,
                Branch: m.SourceBranch,
                State: m.Status.ToString(),
                IsStatusManual: m.IsStatusManual,
                Labels: m.Labels.Split(',', StringSplitOptions.RemoveEmptyEntries),
                PipelineResult: m.PipelineResult,
                Readiness: readiness?.Evaluate(m.Id, m.RepositoryId, m.Labels, m.Status, m.PipelineResult),
                At: m.CreatedAt))
            .ToList();

        // A branch a merge request already carries is represented by that merge request, so listing it
        // again would double-count the same work. Same rule the launch guard applies.
        var carried = mergeRequests
            .Select(m => (m.RepositoryId, m.SourceBranch))
            .ToHashSet();

        rows.AddRange(branches
            .Where(b => !carried.Contains((b.RepositoryId, b.Name)))
            .Select(b => new WorkItemDto(
                Kind: WorkItemKind.Branch,
                TaskKey: b.TaskExternalId,
                RepositoryName: b.RepositoryName,
                ConnectionName: b.ConnectionName,
                Carrier: b.Name,
                Branch: b.Name,
                // The earliest state there is: the work exists and has not been raised for review.
                State: "New",
                IsStatusManual: false,
                Labels: [],
                PipelineResult: null,
                // Nothing to judge: readiness is evaluated from a merge request's signals, and there
                // is no merge request. Null says "cannot say", never "not ready".
                Readiness: null,
                At: b.FirstSeenAt)));

        // The column filters narrow the same in-memory rows as the search box above them: this list is
        // assembled from two sources (merge requests and bare branches) and capped, so there is no one
        // query to push them into. They are AND-ed with each other and with the search box, which is
        // what a filter row means everywhere else.
        var filtered = rows
            .Where(r => string.IsNullOrEmpty(state) || string.Equals(r.State, state, StringComparison.OrdinalIgnoreCase))
            .Where(r => Matches(r, search))
            .Where(r => Contains(r.TaskKey, taskKey))
            .Where(r => Contains(r.RepositoryName, repository))
            .Where(r => Contains(r.ConnectionName, connection))
            .Where(r => Contains(r.Branch, branch) || Contains(r.Carrier, branch))
            // Grouped by task so a task's work reads together, which is the question this page answers.
            // Items with no task sort last: they are in no plan, and that is the anomaly to notice.
            .OrderBy(r => r.TaskKey is null ? 1 : 0)
            .ThenBy(r => r.TaskKey, StringComparer.Ordinal)
            .ThenBy(r => r.RepositoryName, StringComparer.Ordinal)
            .ThenBy(r => r.Carrier, StringComparer.Ordinal)
            .ToList();

        return Ok(new WorkItemsResult(
            filtered.Count,
            paging.Page,
            paging.PageSize,
            filtered.Skip(paging.Skip).Take(paging.PageSize).ToList(),
            // Stated rather than left to be discovered: past the cap this list is a slice.
            Truncated: mergeRequests.Count >= ScanCap || branches.Count >= ScanCap));
    }

    /// <summary>One column filter box against one value; a blank box selects everything.</summary>
    private static bool Contains(string? value, string? needle) =>
        string.IsNullOrWhiteSpace(needle)
        || (value?.Contains(needle.Trim(), StringComparison.OrdinalIgnoreCase) ?? false);

    private static bool Matches(WorkItemDto row, string? search) =>
        string.IsNullOrWhiteSpace(search)
        || (row.TaskKey?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false)
        || row.RepositoryName.Contains(search, StringComparison.OrdinalIgnoreCase)
        || row.Branch.Contains(search, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Everything needed to answer "is this ready for that environment", or null when no environment
    /// was named.
    /// </summary>
    /// <remarks>
    /// Resolved exactly as the launch gate resolves it - pin, then the repository's override for this
    /// environment, then the environment's default, then no gate - because a screen that disagreed
    /// with the gate about what may deploy would be worse than a screen that said nothing.
    /// </remarks>
    private async Task<ReadinessView?> LoadReadinessAsync(Guid? environmentId, CancellationToken ct)
    {
        if (environmentId is not { } envId) return null;

        var environment = await db.DeploymentEnvironments
            .Where(e => e.Id == envId)
            .Select(e => new { e.Id, e.ReadinessRuleId })
            .AsNoTracking()
            .FirstOrDefaultAsync(ct);

        if (environment is null) return null;

        var overrides = (await db.RepositoryDeployTargets
                .Where(t => t.EnvironmentId == envId)
                .Select(t => new { t.RepositoryId, t.ReadinessRuleId })
                .AsNoTracking()
                .ToListAsync(ct))
            .ToDictionary(t => t.RepositoryId, t => t.ReadinessRuleId);

        var rules = (await db.ReadinessRules
                .Select(r => new { r.Id, r.Mode, r.RequiredSignals })
                .AsNoTracking()
                .ToListAsync(ct))
            .ToDictionary(r => r.Id, r => (r.Mode, r.RequiredSignals));

        var pins = (await db.MergeRequestReadinessPins
                .Where(p => p.EnvironmentId == envId)
                .Select(p => new { p.MergeRequestId, p.IsReady })
                .AsNoTracking()
                .ToListAsync(ct))
            .ToDictionary(p => p.MergeRequestId, p => (bool?)p.IsReady);

        var deployed = (await db.MrDeploymentStates
                .AsNoTracking()
                .Where(s => s.EnvironmentId == envId
                            && (s.State == DeploymentState.Deployed || s.State == DeploymentState.Skipped))
                .Select(s => s.MergeRequestId)
                .ToListAsync(ct))
            .ToHashSet();

        return new ReadinessView(environment.ReadinessRuleId, overrides, rules, pins, deployed);
    }

    /// <summary>The environment's readiness configuration, resolved per merge request.</summary>
    private sealed record ReadinessView(
        Guid? EnvironmentRuleId,
        Dictionary<Guid, Guid?> RepositoryOverrides,
        Dictionary<Guid, (ReadyRule Mode, string RequiredSignals)> Rules,
        Dictionary<Guid, bool?> Pins,
        HashSet<Guid> Deployed)
    {
        public WorkItemReadinessDto Evaluate(
            Guid mergeRequestId, Guid repositoryId, string labels, MergeRequestStatus status, string? pipelineResult)
        {
            if (Deployed.Contains(mergeRequestId))
                return new WorkItemReadinessDto(WorkItemReadiness.Deployed, true, []);

            var ruleId = RepositoryOverrides.GetValueOrDefault(repositoryId) ?? EnvironmentRuleId;
            var pin = Pins.GetValueOrDefault(mergeRequestId);

            // No rule resolved: ungated, so ready unless a pin deliberately holds it.
            if (ruleId is not { } id || !Rules.TryGetValue(id, out var rule))
                return new WorkItemReadinessDto(
                    pin is false ? WorkItemReadiness.Held : WorkItemReadiness.Ungated, pin ?? true, []);

            var signals = ReadinessSignals.For(
                labels.Split(',', StringSplitOptions.RemoveEmptyEntries), status, pipelineResult);
            var required = rule.RequiredSignals.Split(',', StringSplitOptions.RemoveEmptyEntries);
            var decision = ReadinessEvaluator.Evaluate(signals, required, rule.Mode, pin);

            // The signals still wanted, so the answer says what to do rather than only that it is no.
            var missing = decision.IsReady
                ? []
                : required.Where(r => !signals.Contains(r)).ToArray();

            var readiness = decision.Source == ReadinessSource.Pin
                ? (decision.IsReady ? WorkItemReadiness.Pinned : WorkItemReadiness.Held)
                : (decision.IsReady ? WorkItemReadiness.Ready : WorkItemReadiness.NotReady);

            return new WorkItemReadinessDto(readiness, decision.IsReady, missing);
        }
    }
}

