namespace Echelon.Core.Enums;

/// <summary>The lifecycle of one rollout step (one merge request's deploy within a run).</summary>
public enum RolloutStepState
{
    /// <summary>Waiting for its wave and its predecessors.</summary>
    Pending = 1,

    /// <summary>The single-writer claim is held; about to start the deploy.</summary>
    Claimed = 2,

    /// <summary>The deploy strategy has been started.</summary>
    Deploying = 3,

    /// <summary>An async strategy (a pipeline) is running; the watcher polls it.</summary>
    Awaiting = 4,

    /// <summary>Deployed successfully.</summary>
    Succeeded = 5,

    /// <summary>Failed past its retry budget.</summary>
    Failed = 6,

    /// <summary>Not executed: already deployed in this environment, or skipped by an operator.</summary>
    Skipped = 7
}
