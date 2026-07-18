namespace ReleaseOrchestrator.Providers.Abstractions.Tracker;

/// <summary>
/// An optional capability of a tracker provider: it can write, not only read.
/// </summary>
/// <remarks>
/// Probed with an <c>is</c> check, the same template as <see cref="ITrackerDependencySource"/>, so a
/// read-only tracker stays read-only and the tracker-status / tracker-comment action handlers simply
/// do nothing for a provider that does not implement this (docs/issues/005-extension-model.md).
/// </remarks>
public interface ITrackerMutator
{
    /// <summary>Moves an issue to a status.</summary>
    /// <param name="issueKey">The issue key.</param>
    /// <param name="statusKey">The target status, in the tracker's own vocabulary.</param>
    /// <param name="ct">Cancellation token.</param>
    Task SetStatusAsync(string issueKey, string statusKey, CancellationToken ct);

    /// <summary>Adds a comment to an issue.</summary>
    /// <param name="issueKey">The issue key.</param>
    /// <param name="comment">The comment text.</param>
    /// <param name="ct">Cancellation token.</param>
    Task AddCommentAsync(string issueKey, string comment, CancellationToken ct);
}
