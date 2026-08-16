namespace Echelon.Providers.Abstractions.Tracker;

/// <summary>
/// A tracker issue, normalized.
/// </summary>
/// <param name="Key">The issue key, as the tracker spells it.</param>
/// <param name="Summary">Human-readable summary.</param>
/// <param name="StatusKey">
/// The tracker's own status key, kept raw on purpose: status vocabularies are per-tracker and
/// per-workflow (Jira lets each project invent its own), so there is no normalized set to map to.
/// Ask <see cref="ITrackerProvider.IsClosedStatus"/> rather than comparing this to a literal.
/// </param>
/// <param name="ResolvedAt">When the tracker says it was resolved, when it says so.</param>
/// <param name="ParentKey">
/// The key of this issue's parent in the tracker's hierarchy - an epic over its subtasks - or null
/// when the issue is top-level or the tracker has no hierarchy.
/// <para>
/// Carries ordering, which is why it is here rather than left as display metadata: a parent is the
/// umbrella over concrete work, so its children deploy first and it deploys last. The orchestrator
/// turns this into the same kind of edge a declared dependency produces.
/// </para>
/// <para>
/// Positional and without a default on purpose. An adapter that forgets it does not produce a task
/// with no parent - it produces a plan missing an ordering constraint, which looks exactly like a
/// correct plan. The compiler naming every construction site is the cheapest way to keep that from
/// happening quietly.
/// </para>
/// </param>
public sealed record TrackerIssue(
    string Key,
    string Summary,
    string StatusKey,
    DateTime? ResolvedAt,
    string? ParentKey);

/// <summary>
/// A "depends on" edge between two issues.
/// </summary>
/// <remarks>
/// <para>
/// One edge type, already normalized to the direction the planner needs: <paramref name="IssueKey"/>
/// waits for <paramref name="DependsOnKey"/>.
/// </para>
/// <para>
/// Deliberately not a general link model. Jira, Yandex.Tracker and Linear all express "blocks" /
/// "depends on", and it is the only relation that carries ordering - <c>relates</c> exists
/// everywhere but means nothing, and feeding it to a topological sort invents constraints that
/// were never stated. Atlassian is explicit that link direction is only interpretable at the UI
/// level, so resolving which end waits for which is the adapter's job, done before this record
/// is built.
/// </para>
/// </remarks>
/// <param name="IssueKey">The dependent issue - the one that waits.</param>
/// <param name="DependsOnKey">The prerequisite issue.</param>
public sealed record TrackerIssueDependency(
    string IssueKey,
    string DependsOnKey);
