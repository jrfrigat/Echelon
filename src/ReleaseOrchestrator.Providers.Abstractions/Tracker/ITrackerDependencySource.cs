namespace ReleaseOrchestrator.Providers.Abstractions.Tracker;

/// <summary>
/// A tracker that can report dependency edges between issues.
/// </summary>
/// <remarks>
/// <para>
/// Separate from <see cref="ITrackerProvider"/> because this is the "can the provider do it at
/// all" question, and the answer is a property of the provider type rather than of the install:
/// a tracker either models issue links or it does not. Callers ask with an <c>is</c> check -
/// <c>if (provider is ITrackerDependencySource source)</c> - which the compiler settles, instead
/// of calling and interpreting a failure.
/// </para>
/// <para>
/// Not folded into the main port because a tracker without links would have to implement it and
/// return empty, which reads identically to "this issue has no dependencies". go-scm signals the
/// same thing with an <c>ErrNotSupported</c> sentinel, but that only answers after the call -
/// too late for a planner deciding whether task edges are available at all.
/// </para>
/// </remarks>
public interface ITrackerDependencySource
{
    /// <summary>Reads the issues that <paramref name="issueKey"/> depends on.</summary>
    /// <param name="issueKey">The dependent issue's key.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The edges, normalized so the planner can order them; empty when there are none.</returns>
    Task<IReadOnlyList<TrackerIssueDependency>> GetIssueDependenciesAsync(string issueKey, CancellationToken ct);
}
