namespace Echelon.Core.Enums;

/// <summary>
/// How one piece of work stands against one environment's readiness rule.
/// </summary>
/// <remarks>
/// Five answers, not two, because "not ready" and "nobody is asking" are different facts and a screen
/// that renders them the same teaches an operator to distrust it. A pin is called out separately for
/// the same reason: it is a person's decision, not the rule's.
/// </remarks>
public enum WorkItemReadiness
{
    /// <summary>The rule's signals are all present.</summary>
    Ready = 0,

    /// <summary>The rule is not satisfied; what is missing is listed alongside.</summary>
    NotReady = 1,

    /// <summary>An operator pinned it ready, whatever the signals say.</summary>
    Pinned = 2,

    /// <summary>An operator pinned it back, whatever the signals say.</summary>
    Held = 3,

    /// <summary>No rule applies to this environment, so nothing gates the deploy.</summary>
    Ungated = 4,

    /// <summary>Already deployed to this environment; the gate is behind it, not ahead.</summary>
    Deployed = 5
}
