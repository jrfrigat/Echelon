namespace Echelon.Application.Services;

/// <summary>Imports tracker issues and their ordering constraints into the local model.</summary>
public interface ITrackerService
{
    /// <summary>
    /// Reads a task and its dependency links from the tracker into the local model.
    /// This is the only thing that produces task edges for the release plan.
    /// </summary>
    /// <param name="trackerConnectionId">The tracker connection to read from.</param>
    /// <param name="externalTaskId">The issue key.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True when the dependency edges changed, so the caller can replan only then.</returns>
    Task<bool> SyncTaskAsync(Guid trackerConnectionId, string externalTaskId, CancellationToken ct = default);

    // ParseTaskIdFromBranchAsync used to sit here, with no callers. A branch name is a VCS
    // artifact and the key format is a dialect, so it now lives behind
    // IVcsProvider.ParseTaskKeyFromBranch, where the provider that spells it can answer.
}
