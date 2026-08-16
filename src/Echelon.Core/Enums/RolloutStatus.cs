namespace Echelon.Core.Enums;

/// <summary>The lifecycle of one rollout run (one target task into one environment).</summary>
public enum RolloutStatus
{
    /// <summary>Materialised, not yet dispatched.</summary>
    Pending = 1,

    /// <summary>Steps are being dispatched and executed.</summary>
    Running = 2,

    /// <summary>A step failed past its retry budget; dispatch is halted pending an operator.</summary>
    Paused = 3,

    /// <summary>Cancellation requested; in-flight steps are being allowed to settle.</summary>
    Cancelling = 4,

    /// <summary>Cancelled; no further steps will run.</summary>
    Cancelled = 5,

    /// <summary>Every step reached a terminal success (Succeeded or Skipped).</summary>
    Succeeded = 6,

    /// <summary>Terminated with at least one failed step and no path forward.</summary>
    Failed = 7
}
