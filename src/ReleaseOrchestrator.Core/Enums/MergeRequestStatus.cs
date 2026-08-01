namespace ReleaseOrchestrator.Core.Enums;

/// <summary>
/// Where a merge request stands, normalized. Providers spell their own states
/// (<c>opened</c>/<c>merged</c>/<c>closed</c>, <c>OPEN</c>/<c>DECLINED</c>, an open flag plus a
/// merged flag); each adapter translates into this, and only this crosses into the domain.
/// </summary>
/// <remarks>
/// <para>
/// Persisted as TEXT, not as its numeric value — see the value conversion in
/// <c>AppDbContext.OnModelCreating</c>. That is what makes reordering or renumbering these members
/// safe and renaming one a breaking change, which is the opposite of the usual expectation.
/// </para>
/// <para>
/// <see cref="Reviewed"/> and <see cref="ReadyForDeploy"/> are RETIRED: nothing assigns them any
/// more. Deploy readiness became a per-environment rule evaluated at launch
/// (<see cref="Parsing.ReadinessEvaluator"/>) rather than a status a label promoted a merge request
/// into, and <c>ResolveOpenStatus</c> now answers <see cref="Opened"/> for every open merge request.
/// They stay declared because the column holds their names in rows written before the change, and
/// deleting a member would make those rows fail to materialise. Treat them as read-only history; do
/// not write them. See docs/issues/011-release-audit.md.
/// </para>
/// </remarks>
public enum MergeRequestStatus
{
    /// <summary>Open at the provider. The status of every merge request that is not terminal.</summary>
    Opened = 1,

    /// <summary>Retired. Only ever set by the label promotion that no longer exists.</summary>
    Reviewed = 2,

    /// <summary>Retired. Superseded by the per-environment readiness rule.</summary>
    ReadyForDeploy = 3,

    /// <summary>Merged at the provider. Terminal, and never reversed: a merge cannot be undone.</summary>
    Merged = 4,

    /// <summary>Closed without merging. Terminal, but reversible — a provider can reopen it.</summary>
    Closed = 5
}
