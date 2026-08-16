namespace Echelon.Providers.Abstractions.Deploy;

/// <summary>The result kind of a deploy attempt.</summary>
public enum DeployOutcome
{
    /// <summary>The deploy finished successfully.</summary>
    Succeeded = 1,

    /// <summary>The deploy failed.</summary>
    Failed = 2,

    /// <summary>Started but not finished -- an async pipeline is running; poll it.</summary>
    Awaiting = 3,

    /// <summary>Already deployed; nothing to do. Idempotent success.</summary>
    AlreadyDone = 4
}
