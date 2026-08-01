namespace ReleaseOrchestrator.Providers.Abstractions.Vcs;

/// <summary>
/// A branch, normalized: enough to tell whether work exists for a task and whether it has landed.
/// </summary>
/// <remarks>
/// A branch matters to planning even when no merge request has been raised for it: it is work that
/// has started and not landed, so a task that owns one is not finished — and a parent whose child
/// still has such a branch cannot be rolled out. A branch that already has an open merge request is
/// represented in the plan by that merge request; a bare one is the case this type exists to surface.
/// </remarks>
/// <param name="Name">The branch name, as the provider spells it.</param>
/// <param name="IsMerged">Whether the provider reports it as merged into the default branch.</param>
/// <param name="IsDefault">Whether it is the repository's default branch, which never counts as work.</param>
public sealed record VcsBranch(string Name, bool IsMerged, bool IsDefault = false);
