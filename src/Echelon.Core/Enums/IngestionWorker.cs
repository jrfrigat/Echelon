namespace Echelon.Core.Enums;

/// <summary>
/// A background worker that brings the outside world in.
/// </summary>
/// <remarks>
/// Named rather than free text, because the admin screen groups by these and an operator asking "did
/// anything read the tracker in the last hour" needs the answer to be about the same worker every
/// time. Only the pull side appears here: what arrives by webhook is recorded as a signal instead,
/// since no worker of ours decides when it happens.
/// </remarks>
public enum IngestionWorker
{
    /// <summary>Sweeps poll-mode VCS connections for merge requests and branches.</summary>
    VcsPolling = 0,

    /// <summary>Sweeps poll-mode tracker connections: what is open, and what is already known.</summary>
    TrackerPolling = 1,

    /// <summary>The slower pass that re-reads task links no webhook announces.</summary>
    TaskReconciliation = 2
}
