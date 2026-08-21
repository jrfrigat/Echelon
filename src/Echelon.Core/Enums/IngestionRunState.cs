namespace Echelon.Core.Enums;

/// <summary>Where a background worker stands right now.</summary>
public enum IngestionRunState
{
    /// <summary>Configured off. It will never run, which is a fact worth showing rather than an empty row.</summary>
    Disabled = 0,

    /// <summary>Waiting for its next tick.</summary>
    Idle = 1,

    /// <summary>Working through a pass.</summary>
    Running = 2,

    /// <summary>
    /// Enabled, but this replica is not the one doing it: the sweep is leased, so exactly one replica
    /// holds it and the others wait.
    /// </summary>
    NotLeader = 3
}
