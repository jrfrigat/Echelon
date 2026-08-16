namespace Echelon.Infrastructure.Execution;

/// <summary>Tunables for the rollout coordinator and the launch gates.</summary>
public class RolloutExecutionOptions
{
    /// <summary>Whether the coordinator background service runs. Off in tests.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>How often the coordinator drives running rollouts.</summary>
    public int IntervalSeconds { get; set; } = 15;

    /// <summary>How many steps of the current wave may deploy at once.</summary>
    public int WaveConcurrency { get; set; } = 4;

    /// <summary>How many times a step is attempted before the run pauses for an operator.</summary>
    public int MaxAttempts { get; set; } = 3;

    /// <summary>
    /// How long a single in-flight deploy (Awaiting/polling) may run before it is given up as failed.
    /// Bounds a stuck pipeline and a persistent poll outage so a step cannot re-poll forever.
    /// </summary>
    public int MaxDeployMinutes { get; set; } = 120;

    /// <summary>
    /// When true, a rollout to an environment requires the closure already deployed to every enabled
    /// environment ordered before it (prod requires staging). Editable, on by default.
    /// </summary>
    public bool EnvironmentProgressionGate { get; set; } = true;
}
