namespace Echelon.Providers.Abstractions.Vcs;

/// <summary>
/// A VCS that can say which CI jobs a repository's pipelines actually run.
/// </summary>
/// <remarks>
/// <para>
/// Optional, and asked with an <c>is</c> check - <c>if (provider is IPipelineJobSource source)</c> -
/// like <c>ITrackerIssueSource</c> on the tracker side: a VCS without CI has no answer, and an empty
/// list from it would read as "this repository runs no jobs" rather than "I cannot tell you".
/// </para>
/// <para>
/// It exists for one screen. A deploy target that runs a named job needs the name spelled exactly,
/// and the name lives in a CI file the operator is not looking at - or, with a <c>parallel: matrix</c>
/// job, is not even written there in the form the pipeline uses. Recalling it correctly is not a
/// reasonable thing to ask, so the form offers what the pipelines actually contain. The same argument
/// as the readiness signal chips, one screen over.
/// </para>
/// <para>
/// Names only. Whether a job can be played, and what happens if it already ran, is the deploy
/// strategy's business at deploy time; this answers the question the form is asking, which is how the
/// job is spelled.
/// </para>
/// </remarks>
public interface IPipelineJobSource
{
    /// <summary>Lists the distinct job names of the repository's most recent pipelines.</summary>
    /// <param name="repositoryExternalId">The repository, as the provider identifies it.</param>
    /// <param name="limit">The most names to return.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The names, de-duplicated and ordered; empty when the repository has no pipeline yet.</returns>
    Task<IReadOnlyList<string>> ListRecentJobNamesAsync(string repositoryExternalId, int limit, CancellationToken ct);
}
