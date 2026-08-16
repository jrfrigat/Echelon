using Echelon.Core.Enums;

namespace Echelon.Application.ReleasePlanning;

/// <summary>
/// The ordering rule document, as a model. One-to-one with the YAML in
/// docs/issues/012-ordering-rules.md - the text is a thin mapping onto this, and this is what
/// decides what the document means.
/// </summary>
/// <param name="Version">Schema version; only <c>1</c> exists.</param>
/// <param name="Tasks">The wait policy and its per-task exceptions.</param>
/// <param name="Groups">Named selectors over discovered work, keyed by group name.</param>
/// <param name="Order">Ordering between groups.</param>
public sealed record OrderingRules(
    int Version,
    TaskPolicySpec Tasks,
    IReadOnlyDictionary<string, WorkSelector> Groups,
    IReadOnlyList<GroupOrderSpec> Order)
{
    /// <summary>An empty document: no groups, no ordering, the default wait policy.</summary>
    /// <remarks>
    /// What an installation that has configured nothing plans by, so adding the feature changes no
    /// existing plan until somebody writes a rule.
    /// </remarks>
    public static OrderingRules Empty { get; } =
        new(1, new TaskPolicySpec(null, null, null, []), new Dictionary<string, WorkSelector>(), []);
}

/// <summary>The wait policy from the document, plus the selectors that override it per task.</summary>
/// <param name="WaitForSubtasks">Null means "leave the stored default alone".</param>
/// <param name="WaitForLinked">Null means "leave the stored default alone".</param>
/// <param name="GroupOrder">Null means "leave the stored default alone".</param>
/// <param name="Overrides">Exceptions, each a selector plus the answers it imposes.</param>
public sealed record TaskPolicySpec(
    bool? WaitForSubtasks,
    bool? WaitForLinked,
    PrerequisiteGroupOrder? GroupOrder,
    IReadOnlyList<TaskPolicyOverride> Overrides);

/// <summary>An exception to the document's wait policy, for the tasks a selector matches.</summary>
/// <param name="Match">Which tasks this applies to.</param>
/// <param name="WaitForSubtasks">The answer to impose, or null to leave as resolved.</param>
/// <param name="WaitForLinked">The answer to impose, or null to leave as resolved.</param>
/// <param name="GroupOrder">The answer to impose, or null to leave as resolved.</param>
public sealed record TaskPolicyOverride(
    WorkSelector Match,
    bool? WaitForSubtasks,
    bool? WaitForLinked,
    PrerequisiteGroupOrder? GroupOrder);

/// <summary>
/// A selector over discovered work. Axes combine with AND; values within an axis with OR.
/// </summary>
/// <remarks>
/// Selectors rather than a list of units because the units are not authored - tasks, merge requests
/// and branches arrive from connectors and change on every ingestion event, so anything enumerated in
/// a document is stale by the next webhook.
/// </remarks>
/// <param name="Connectors">Connection names; glob.</param>
/// <param name="Repositories">Repository external ids such as <c>group/project</c>; glob.</param>
/// <param name="Branches">Branch names - a merge request's source branch; glob.</param>
/// <param name="TaskKeys">Task keys; glob.</param>
/// <param name="Labels">Labels a merge request carries; exact, since a label is an identifier.</param>
/// <param name="Exclude">A nested selector subtracted from this one, or null.</param>
public sealed record WorkSelector(
    IReadOnlyList<string> Connectors,
    IReadOnlyList<string> Repositories,
    IReadOnlyList<string> Branches,
    IReadOnlyList<string> TaskKeys,
    IReadOnlyList<string> Labels,
    WorkSelector? Exclude = null)
{
    /// <summary>A selector with no axes set. Matches everything - used only as a base to build on.</summary>
    public static WorkSelector Any { get; } = new([], [], [], [], []);

    /// <summary>True when no axis is set, which is what makes a selector match everything.</summary>
    public bool IsUnconstrained =>
        Connectors.Count == 0 && Repositories.Count == 0 && Branches.Count == 0
        && TaskKeys.Count == 0 && Labels.Count == 0;

    /// <summary>Whether this selector admits the candidate.</summary>
    /// <param name="candidate">The piece of work being tested.</param>
    public bool Matches(OrderingCandidate candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        if (!MatchesAxis(Connectors, candidate.ConnectionName)) return false;
        if (!MatchesAxis(Repositories, candidate.RepositoryExternalId)) return false;
        if (!MatchesAxis(Branches, candidate.Branch)) return false;
        if (!MatchesAxis(TaskKeys, candidate.TaskKey)) return false;

        // Labels are exact: a label is an identifier, and a glob over it would quietly widen a gate.
        if (Labels.Count > 0 && !Labels.Any(l => candidate.Labels.Contains(l, StringComparer.Ordinal)))
            return false;

        return Exclude is null || !Exclude.Matches(candidate);
    }

    /// <summary>An unset axis constrains nothing; a set one needs a match, and null value cannot match.</summary>
    private static bool MatchesAxis(IReadOnlyList<string> patterns, string? value) =>
        patterns.Count == 0 || (value is not null && patterns.Any(p => Glob.IsMatch(p, value)));
}

/// <summary>"This group deploys after those groups", and how firmly.</summary>
/// <param name="Group">The group that waits.</param>
/// <param name="Needs">The groups it waits for.</param>
/// <param name="Type">Hard never yields when a cycle must be broken; soft yields first.</param>
/// <param name="Scope">Whether the ordering reaches across the plan or stays within one task.</param>
public sealed record GroupOrderSpec(
    string Group,
    IReadOnlyList<string> Needs,
    StackDependencyType Type = StackDependencyType.Hard,
    OrderScope Scope = OrderScope.AcrossPlan);

/// <summary>How far an ordering rule reaches.</summary>
/// <remarks>
/// <see cref="AcrossPlan"/> is the default because it is what repository ordering already does, so an
/// existing rule expressed in this language does not change meaning. <see cref="WithinTask"/> exists
/// because the global reach chains tasks that are unrelated: "frontend after backend" applied across a
/// plan orders one task's frontend behind another task's backend, which is a correct plan that has
/// quietly given up parallelism nobody asked to lose.
/// </remarks>
public enum OrderScope
{
    /// <summary>Between any merge requests in the plan.</summary>
    AcrossPlan = 1,

    /// <summary>Only between merge requests of the same task.</summary>
    WithinTask = 2
}

/// <summary>One piece of work a rule can select, reduced to what selectors read.</summary>
/// <param name="MergeRequestId">The merge request the resulting edge will name.</param>
/// <param name="TaskId">Its task, or null when no linking rule matched.</param>
/// <param name="ConnectionName">The connection that reported it.</param>
/// <param name="RepositoryExternalId">The repository, as the provider spells it.</param>
/// <param name="Branch">The merge request's source branch.</param>
/// <param name="TaskKey">The task's key, or null when unlinked.</param>
/// <param name="Labels">Labels it carries, canonical.</param>
public sealed record OrderingCandidate(
    Guid MergeRequestId,
    Guid? TaskId,
    string ConnectionName,
    string RepositoryExternalId,
    string Branch,
    string? TaskKey,
    IReadOnlyList<string> Labels);
